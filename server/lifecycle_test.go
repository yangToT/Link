package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func lifecycleApp(t *testing.T) (*App, *[]string, *bool) {
	a := testApp(t)
	calls := []string{}
	fail := false
	upstream := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls = append(calls, r.Method+" "+r.URL.Path)
		if fail && strings.HasPrefix(r.URL.Path, "/api/groups/") {
			w.WriteHeader(503)
			return
		}
		if r.Method == "DELETE" {
			w.WriteHeader(204)
			return
		}
		switch r.URL.Path {
		case "/api/setup-keys":
			if r.Method == "POST" {
				writeJSON(w, 201, map[string]string{"key": "new-key"})
			} else {
				writeJSON(w, 200, []any{map[string]any{"id": "key", "auto_groups": []string{"owned"}}})
			}
		case "/api/peers":
			writeJSON(w, 200, []any{map[string]any{"id": "peer", "groups": []any{map[string]string{"id": "owned"}}}})
		}
	}))
	t.Cleanup(upstream.Close)
	a.backend.URL = upstream.URL
	a.store.Data.Devices = []Device{{ID: "admin", Role: "admin", State: "active", IP: "100.80.0.2", LastSeen: time.Now()}, {ID: "target", Name: "same-name", Role: "member", State: "active", GroupID: "owned", PeerID: "peer", TokenHash: digest("target-secret-token-longer-than-32-chars"), Connected: true, LastSeen: time.Now()}}
	a.store.Data.Mappings = []Mapping{{ID: "target-map", DeviceID: "target"}, {ID: "other-map", DeviceID: "admin"}}
	return a, &calls, &fail
}
func deleteRequest(a *App) *httptest.ResponseRecorder {
	return request(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { a.deviceAction(w, r, "admin") }), "POST", "/api/device", map[string]string{"id": "target", "action": "delete"}, "")
}
func TestDeviceDeletionRevokeThenRemoveAndRetry(t *testing.T) {
	a, calls, fail := lifecycleApp(t)
	a.store.Data.EntryID = "target"
	a.store.Data.RouteIDs = []string{"route"}
	*fail = true
	if w := deleteRequest(a); w.Code != 502 {
		t.Fatal(w.Code, w.Body.String())
	}
	if a.store.device("target") == nil || len(a.store.Data.Mappings) != 2 {
		t.Fatal("failed cleanup lost ownership")
	}
	*fail = false
	if w := deleteRequest(a); w.Code != 200 {
		t.Fatal(w.Code, w.Body.String())
	}
	if a.store.device("target") != nil || a.store.Data.EntryID != "" || len(a.store.Data.RouteIDs) != 0 || a.store.Data.NetworkMode != "routed" {
		t.Fatal("entry remained")
	}
	if len(a.store.Data.Mappings) != 1 || a.store.Data.Mappings[0].ID != "other-map" {
		t.Fatal("mapping scope incorrect")
	}
	for _, expected := range []string{"DELETE /api/routes/route", "DELETE /api/setup-keys/key", "DELETE /api/peers/peer", "DELETE /api/groups/owned"} {
		if !strings.Contains(strings.Join(*calls, "\n"), expected) {
			t.Fatal(expected, *calls)
		}
	}
	token := "target-secret-token-longer-than-32-chars"
	for _, path := range []string{"/agent/reconnect", "/agent/heartbeat", "/agent/disconnect"} {
		if w := request(a.public(), "POST", path, map[string]any{}, token); w.Code != 403 {
			t.Fatal(path, w.Code)
		}
	}
	restored, err := openStore(a.store.path)
	if err != nil || restored.device("target") != nil {
		t.Fatal("deletion not persisted", err)
	}
}
func TestDeleteRequiresAdminSessionAndProtectsSelf(t *testing.T) {
	a, calls, _ := lifecycleApp(t)
	if w := request(a.private(), "POST", "/api/device", map[string]string{"id": "target", "action": "delete"}, ""); w.Code != 401 && w.Code != 403 {
		t.Fatal(w.Code)
	}
	w := request(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { a.deviceAction(w, r, "target") }), "POST", "/", map[string]string{"id": "target", "action": "delete"}, "")
	if w.Code != 409 || len(*calls) != 0 {
		t.Fatal("self deletion reached backend")
	}
}
func TestDisconnectReconnectKeepsDeviceIdentity(t *testing.T) {
	a, _, _ := lifecycleApp(t)
	token := "target-secret-token-longer-than-32-chars"
	for i := 0; i < 3; i++ {
		if w := request(a.public(), "POST", "/agent/disconnect", nil, token); w.Code != 200 {
			t.Fatal(w.Code, w.Body.String())
		}
		if publicDevice(*a.store.device("target")).Connected || a.store.device("target").State != "paused" {
			t.Fatal("disconnect not immediate")
		}
		if w := request(a.public(), "POST", "/agent/heartbeat", map[string]any{}, token); !strings.Contains(w.Body.String(), "disconnect") {
			t.Fatal("heartbeat reactivated device")
		}
		w := request(a.public(), "POST", "/agent/reconnect", nil, token)
		var result map[string]string
		json.Unmarshal(w.Body.Bytes(), &result)
		if w.Code != 200 || result["setupKey"] != "new-key" || len(a.store.Data.Devices) != 2 || a.store.device("target").TokenHash != digest(token) {
			t.Fatal("reconnect created another identity", w.Body.String())
		}
	}
}
func TestDeletingLayer2EntryRevokesDependants(t *testing.T) {
	a, _, _ := lifecycleApp(t)
	a.store.Data.EntryID = "target"
	a.store.Data.NetworkMode = "bridged"
	revoked := []string{}
	removed := []string{}
	a.cfg.Layer2 = testLayer2(t, func(method string, p map[string]any) any {
		if method == "SetUser" {
			if p["policy:Access_bool"] != false {
				t.Error("revocation enabled access")
			}
			revoked = append(revoked, p["Name_str"].(string))
		}
		if method == "DeleteUser" {
			removed = append(removed, p["Name_str"].(string))
		}
		return map[string]any{}
	})
	if w := deleteRequest(a); w.Code != 200 {
		t.Fatal(w.Code, w.Body.String())
	}
	if len(revoked) != 2 || len(removed) != 1 || removed[0] != layer2User("target") {
		t.Fatal("wrong layer2 cleanup scope", revoked, removed)
	}
}
