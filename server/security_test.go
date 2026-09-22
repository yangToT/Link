package main

import (
	"bytes"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func testApp(t *testing.T) *App {
	t.Helper()
	backend := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/api/groups":
			writeJSON(w, 201, map[string]string{"id": "group"})
		case "/api/setup-keys":
			writeJSON(w, 201, map[string]string{"key": "one-time-upstream"})
		default:
			writeJSON(w, 200, []any{})
		}
	}))
	t.Cleanup(backend.Close)
	store, err := openStore(filepath.Join(t.TempDir(), "state.json"))
	if err != nil {
		t.Fatal(err)
	}
	a := newApp(Config{BackendURL: backend.URL, PublicURL: "https://192.0.2.1:24443"}, store, nil)
	a.pin = strings.Repeat("a", 64)
	return a
}
func request(h http.Handler, method, path string, data any, token string) *httptest.ResponseRecorder {
	b, _ := json.Marshal(data)
	r := httptest.NewRequest(method, path, bytes.NewReader(b))
	if token != "" {
		r.Header.Set("Authorization", "Bearer "+token)
	}
	w := httptest.NewRecorder()
	h.ServeHTTP(w, r)
	return w
}
func TestPublicManagementIsolation(t *testing.T) {
	a := testApp(t)
	for _, path := range []string{"/", "/api/state", "/api/setup", "/session", "/app.js", "/../api/state", "/oauth2/"} {
		w := request(a.public(), "GET", path, nil, "")
		if w.Code == 200 {
			t.Fatalf("public leaked %s", path)
		}
	}
}
func TestEnrollmentOneUseAndBackendFailure(t *testing.T) {
	a := testApp(t)
	code, err := a.createJoin("admin")
	if err != nil {
		t.Fatal(err)
	}
	original := a.backend.URL
	a.backend.URL = "http://127.0.0.1:1"
	if w := request(a.public(), "POST", "/agent/enroll", map[string]string{"code": code, "name": "one", "os": "Windows"}, ""); w.Code != 503 {
		t.Fatal(w.Code)
	}
	a.backend.URL = original
	w := request(a.public(), "POST", "/agent/enroll", map[string]string{"code": code, "name": "one", "os": "Windows"}, "")
	if w.Code != 201 {
		t.Fatal(w.Code, w.Body.String())
	}
	var result map[string]string
	json.Unmarshal(w.Body.Bytes(), &result)
	if a.store.Data.Devices[0].TokenHash == result["token"] || result["token"] == "" {
		t.Fatal("raw token storage")
	}
	if w = request(a.public(), "POST", "/agent/enroll", map[string]string{"code": code, "name": "two"}, ""); w.Code != 401 {
		t.Fatal("join reused", w.Code)
	}
	a.store.Data.Devices[0].State = "disabled"
	if w = request(a.public(), "POST", "/agent/reconnect", nil, result["token"]); w.Code != 403 {
		t.Fatal("disabled device reconnected")
	}
}
func TestPausedDoesNotAutomaticallyReconnect(t *testing.T) {
	a := testApp(t)
	token := secret()
	a.store.Data.Devices = []Device{{ID: "d", State: "paused", TokenHash: digest(token)}}
	w := request(a.public(), "POST", "/agent/heartbeat", map[string]any{}, token)
	if !strings.Contains(w.Body.String(), "disconnect") {
		t.Fatal(w.Body.String())
	}
	if a.store.Data.Devices[0].State != "paused" {
		t.Fatal("heartbeat reactivated paused device")
	}
}
func TestPrivateSessionBoundToPeerAndCSRF(t *testing.T) {
	a := testApp(t)
	a.store.Data.Devices = []Device{{ID: "admin", Role: "admin", State: "active", IP: "100.100.1.2", Connected: true, LastSeen: time.Now()}}
	a.sessions[digest("cookie")] = Session{Device: "admin", CSRF: "csrf", Expires: time.Now().Add(time.Hour)}
	for _, test := range []struct {
		ip, csrf string
		want     int
	}{{"100.100.1.3", "csrf", 401}, {"100.100.1.2", "wrong", 403}, {"100.100.1.2", "csrf", 201}} {
		r := httptest.NewRequest("POST", "https://100.100.1.1:24444/api/joins", strings.NewReader(`{"role":"member"}`))
		r.RemoteAddr = test.ip + ":55555"
		r.AddCookie(&http.Cookie{Name: "__Host-LinkSession", Value: "cookie"})
		r.Header.Set("X-Link-CSRF", test.csrf)
		r.Header.Set("Origin", "https://100.100.1.1:24444")
		w := httptest.NewRecorder()
		a.private().ServeHTTP(w, r)
		if w.Code != test.want {
			t.Fatalf("ip=%s got=%d want=%d body=%s", test.ip, w.Code, test.want, w.Body.String())
		}
	}
}
func TestMappingPortAllocationAndStaleHealth(t *testing.T) {
	a := testApp(t)
	a.store.Data.Devices = []Device{{ID: "target", State: "active"}, {ID: "entry", State: "active"}}
	a.store.Data.EntryID = "entry"
	for _, name := range []string{"alpha", "beta"} {
		w := request(http.HandlerFunc(a.addMapping), "POST", "/", map[string]any{"name": name, "deviceId": "target", "port": 8080}, "")
		if w.Code != 201 {
			t.Fatal(w.Code)
		}
	}
	if a.store.Data.Mappings[0].EntryPort == a.store.Data.Mappings[1].EntryPort {
		t.Fatal("duplicate entry ports")
	}
	if publicDevice(Device{State: "active", Connected: true, LastSeen: time.Now().Add(-time.Minute)}).Connected {
		t.Fatal("stale peer shown online")
	}
	for _, network := range []string{"0.0.0.0/0", "172.18.1.1/24", "8.8.8.0/24", "127.0.0.0/24"} {
		if validNetwork(network) {
			t.Fatal(network)
		}
	}
}

func TestRevokeCoversPendingKeysAndEveryPeer(t *testing.T) {
	var deleted []string
	upstream := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method == "DELETE" {
			deleted = append(deleted, r.URL.Path)
			w.WriteHeader(204)
			return
		}
		if r.URL.Path == "/api/setup-keys" {
			writeJSON(w, 200, []any{map[string]any{"id": "key1", "auto_groups": []string{"owned"}}, map[string]any{"id": "other", "auto_groups": []string{"different"}}})
			return
		}
		writeJSON(w, 200, []any{map[string]any{"id": "peer1", "groups": []any{map[string]string{"id": "owned"}}}, map[string]any{"id": "peer2", "groups": []any{map[string]string{"id": "owned"}}}, map[string]any{"id": "other", "groups": []any{map[string]string{"id": "different"}}}})
	}))
	defer upstream.Close()
	b := Backend{URL: upstream.URL}
	if err := b.revokeGroup("owned"); err != nil {
		t.Fatal(err)
	}
	if strings.Join(deleted, ",") != "/api/setup-keys/key1,/api/peers/peer1,/api/peers/peer2" {
		t.Fatal(deleted)
	}
}

func TestRecoveryJoinDoesNotOverwriteRunningState(t *testing.T) {
	a := testApp(t)
	a.store.Data.Devices = []Device{{ID: "existing", Name: "kept"}}
	code, err := a.queueJoin("admin")
	if err != nil {
		t.Fatal(err)
	}
	a.consumeJoins()
	a.consumeJoins()
	if len(a.store.Data.Devices) != 1 || len(a.store.Data.Joins) != 1 || a.store.Data.Joins[0].Hash != digest(strings.Split(code, ".")[2]) {
		t.Fatal("recovery overwrote or duplicated state")
	}
}
