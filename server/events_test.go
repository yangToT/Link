package main

import (
	"bufio"
	"context"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func nextEvent(t *testing.T, reader *bufio.Reader) (string, map[string]any) {
	t.Helper()
	type result struct {
		kind  string
		value map[string]any
		err   error
	}
	ready := make(chan result, 1)
	go func() {
		kind := ""
		var value map[string]any
		for {
			line, e := reader.ReadString('\n')
			if e != nil {
				ready <- result{err: e}
				return
			}
			line = strings.TrimSpace(line)
			if strings.HasPrefix(line, "event: ") {
				kind = strings.TrimPrefix(line, "event: ")
			}
			if strings.HasPrefix(line, "data: ") {
				if e := json.Unmarshal([]byte(strings.TrimPrefix(line, "data: ")), &value); e != nil {
					ready <- result{err: e}
					return
				}
			}
			if line == "" && value != nil {
				ready <- result{kind, value, nil}
				return
			}
		}
	}()
	select {
	case result := <-ready:
		if result.err != nil {
			t.Fatal(result.err)
		}
		return result.kind, result.value
	case <-time.After(3 * time.Second):
		t.Fatal("event not delivered promptly")
		return "", nil
	}
}
func TestAgentStreamPushReconnectRevoke(t *testing.T) {
	a := testApp(t)
	token := secret()
	a.store.Data.Devices = []Device{{ID: "one", TokenHash: digest(token), State: "active", Connected: true, LastSeen: time.Now()}}
	server := httptest.NewServer(a.public())
	defer server.Close()
	connect := func() (*http.Response, *bufio.Reader) {
		t.Helper()
		r, _ := http.NewRequest("GET", server.URL+"/agent/events", nil)
		r.Header.Set("Authorization", "Bearer "+token)
		response, e := server.Client().Do(r)
		if e != nil {
			t.Fatal(e)
		}
		if response.StatusCode != 200 {
			response.Body.Close()
			t.Fatal(response.StatusCode)
		}
		return response, bufio.NewReader(response.Body)
	}
	first, reader := connect()
	defer first.Body.Close()
	kind, state := nextEvent(t, reader)
	if kind != "state" || state["action"] != "keep" {
		t.Fatal(kind, state)
	}
	raw, _ := json.Marshal(state)
	if strings.Contains(string(raw), digest(token)) || strings.Contains(string(raw), token) {
		t.Fatal("token leaked in stream")
	}
	a.store.Lock()
	a.store.Data.NetworkMode = "bridged"
	if e := a.store.persist(); e != nil {
		t.Fatal(e)
	}
	a.store.Unlock()
	_, state = nextEvent(t, reader)
	if state["networkMode"] != "bridged" {
		t.Fatal(state)
	}
	first.Body.Close()
	second, reader := connect()
	defer second.Body.Close()
	_, state = nextEvent(t, reader)
	if state["networkMode"] != "bridged" {
		t.Fatal("reconnect missed current state")
	}
	a.store.Lock()
	a.store.Data.Devices[0].State = "disabled"
	a.events.notify()
	a.store.Unlock()
	kind, _ = nextEvent(t, reader)
	if kind != "revoked" {
		t.Fatal(kind)
	}
	if _, e := reader.ReadByte(); e != io.EOF {
		t.Fatal("revoked stream was not closed", e)
	}
	if w := request(a.public(), "GET", "/agent/events", nil, token); w.Code != 403 {
		t.Fatal("disabled stream accepted")
	}
	if w := request(a.public(), "GET", "/api/events", nil, token); w.Code != 404 {
		t.Fatal("admin stream exposed publicly")
	}
}
func TestEventSignatureAndFrozenState(t *testing.T) {
	a := testApp(t)
	a.store.Data.Devices = []Device{{ID: "one", State: "active", Connected: true, LastSeen: time.Now()}}
	a.store.Data.Mappings = []Mapping{{ID: "m", State: "pending"}}
	first := freezeState(a.adminState("csrf"))
	before := stateSignature(first)
	a.store.Data.Devices[0].LastSeen = time.Now().Add(time.Second)
	a.events.notify()
	if before != stateSignature(a.adminState("csrf")) {
		t.Fatal("unchanged heartbeat produced a state event")
	}
	a.store.Data.Mappings[0].State = "ready"
	if before == stateSignature(a.adminState("csrf")) {
		t.Fatal("mapping change not observed")
	}
	if before != stateSignature(first) {
		t.Fatal("snapshot aliases mutable store")
	}
	a.store.Data.Devices[0].LastSeen = time.Now().Add(-time.Minute)
	if stateSignature(a.adminState("csrf")) == before {
		t.Fatal("expiry ignored")
	}
}
func TestAdminStreamSessionAndShutdown(t *testing.T) {
	a := testApp(t)
	a.store.Data.Devices = []Device{{ID: "admin", Role: "admin", State: "active", IP: "127.0.0.1", Connected: true, LastSeen: time.Now()}}
	a.sessions[digest("cookie")] = Session{Device: "admin", CSRF: "csrf", Expires: time.Now().Add(time.Hour)}
	server := httptest.NewServer(a.private())
	defer server.Close()
	if w := request(a.private(), "GET", "/api/events", nil, ""); w.Code != 401 {
		t.Fatal("anonymous stream accepted")
	}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	r, _ := http.NewRequestWithContext(ctx, "GET", server.URL+"/api/events", nil)
	r.AddCookie(&http.Cookie{Name: "__Host-LinkSession", Value: "cookie"})
	response, e := server.Client().Do(r)
	if e != nil {
		t.Fatal(e)
	}
	defer response.Body.Close()
	reader := bufio.NewReader(response.Body)
	_, state := nextEvent(t, reader)
	if state["csrf"] != "csrf" {
		t.Fatal(state)
	}
	a.sessionMu.Lock()
	delete(a.sessions, digest("cookie"))
	a.sessionMu.Unlock()
	a.events.notify()
	kind, _ := nextEvent(t, reader)
	if kind != "revoked" {
		t.Fatal("expired session still streams")
	}
	close(a.events.done)
}
func TestStreamConnectionLimit(t *testing.T) {
	h := newEventHub()
	for i := 0; i < 4; i++ {
		if !h.acquire("same") {
			t.Fatal(i)
		}
	}
	if h.acquire("same") {
		t.Fatal("unbounded streams")
	}
	h.release("same")
	if !h.acquire("same") {
		t.Fatal("slot not released")
	}
}

func TestHTTP2IdleStreamSurvivesWriteDeadline(t *testing.T) {
	a := testApp(t)
	token := secret()
	a.store.Data.Devices = []Device{{ID: "one", State: "active", TokenHash: digest(token), Connected: true, LastSeen: time.Now()}}
	server := httptest.NewUnstartedServer(a.public())
	server.EnableHTTP2 = true
	server.StartTLS()
	defer server.Close()
	r, _ := http.NewRequest("GET", server.URL+"/agent/events", nil)
	r.Header.Set("Authorization", "Bearer "+token)
	response, e := server.Client().Do(r)
	if e != nil {
		t.Fatal(e)
	}
	defer response.Body.Close()
	if response.ProtoMajor != 2 {
		t.Fatal("HTTP/2 fixture did not negotiate")
	}
	reader := bufio.NewReader(response.Body)
	nextEvent(t, reader)
	ready := make(chan string, 1)
	go func() {
		line, e := reader.ReadString('\n')
		if e != nil {
			ready <- e.Error()
			return
		}
		ready <- line
	}()
	select {
	case line := <-ready:
		if line != ": keepalive\n" {
			t.Fatal("idle stream closed or emitted redundant state:", line)
		}
	case <-time.After(18 * time.Second):
		t.Fatal("missing SSE keepalive")
	}
	close(a.events.done)
}
