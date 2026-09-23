package main

import (
	"encoding/json"
	"encoding/pem"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func testLayer2(t *testing.T, call func(string, map[string]any) any) *Layer2Config {
	t.Helper()
	s := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("X-VPNADMIN-PASSWORD") != strings.Repeat("x", 32) {
			t.Error("missing administrator authentication")
		}
		var req struct {
			Method string         `json:"method"`
			Params map[string]any `json:"params"`
		}
		if json.NewDecoder(r.Body).Decode(&req) != nil {
			t.Error("invalid RPC body")
			return
		}
		result := any(map[string]any{})
		if call != nil {
			result = call(req.Method, req.Params)
		}
		writeJSON(w, 200, map[string]any{"jsonrpc": "2.0", "id": "link", "result": result})
	}))
	t.Cleanup(s.Close)
	return &Layer2Config{Endpoint: "100.88.0.1:24448", APIURL: s.URL + "/api/", Hub: "LINK", Password: strings.Repeat("x", 32), Certificate: string(pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: s.Certificate().Raw}))}
}

func TestLayer2PinAndConfig(t *testing.T) {
	c := testLayer2(t, nil)
	if e := c.rpc("GetHub", nil, nil); e != nil {
		t.Fatal(e)
	}
	for _, endpoint := range []string{"192.0.2.1:24448", "100.88.0.1:0", "100.88.0.1:65536", "100.88.0.1:abc"} {
		bad := *c
		bad.Endpoint = endpoint
		if bad.validate() == nil {
			t.Fatal("accepted", endpoint)
		}
	}
	bad := *c
	bad.APIURL = "https://192.0.2.1:24448/api/"
	if bad.validate() == nil {
		t.Fatal("remote management accepted")
	}
	// A valid but different certificate must fail the pin, even with a working TLS endpoint.
	bad = *c
	block, _ := pem.Decode([]byte(bad.Certificate))
	block.Bytes[len(block.Bytes)-1] ^= 1
	bad.Certificate = string(pem.EncodeToMemory(block))
	if bad.rpc("GetHub", nil, nil) == nil {
		t.Fatal("wrong pin accepted")
	}
}

func TestLayer2AuthorizationAndRevocation(t *testing.T) {
	a := testApp(t)
	token := secret()
	var calls []string
	a.cfg.Layer2 = testLayer2(t, func(method string, p map[string]any) any {
		calls = append(calls, method)
		if method == "SetUser" {
			if p["Auth_Password_str"] == a.cfg.Layer2.Password || p["policy:AutoDisconnect_u32"] != float64(0) {
				t.Error("credential isolation or persistent-session policy")
			}
			if p["Name_str"] == layer2User("member") && p["policy:DHCPNoServer_bool"] != true {
				t.Error("member can serve DHCP")
			}
		}
		if method == "EnumSession" {
			return map[string]any{"SessionList": []any{map[string]string{"Name_str": "mine", "Username_str": layer2User("member")}, map[string]string{"Name_str": "foreign", "Username_str": "other"}}}
		}
		if method == "DeleteSession" && p["Name_str"] != "mine" {
			t.Error("deleted foreign session")
		}
		return map[string]any{}
	})
	a.store.Data.NetworkMode = "bridged"
	a.store.Data.EntryID = "entry"
	a.store.Data.Devices = []Device{{ID: "entry", State: "active", Connected: true, LastSeen: time.Now(), LANIP: "192.168.20.2", Networks: []string{"192.168.20.0/24"}, Layer2: Layer2Status{Enabled: true, Prepared: true}, Network: NetworkReport{BridgeEligible: true}}, {ID: "member", State: "active", Connected: true, LastSeen: time.Now(), TokenHash: digest(token), Layer2: Layer2Status{Enabled: true, Prepared: true}}}
	w := request(a.public(), "POST", "/agent/layer2", nil, token)
	if w.Code != 200 {
		t.Fatal(w.Code, w.Body.String())
	}
	if strings.Contains(w.Body.String(), a.cfg.Layer2.Password) {
		t.Fatal("administrator password leaked")
	}
	for _, status := range []Layer2Status{{Enabled: false, Prepared: true}, {Enabled: true, Prepared: false}} {
		a.store.Data.Devices[1].Layer2 = status
		before := len(calls)
		response := request(a.public(), "POST", "/agent/layer2", nil, token)
		if response.Code != 200 || strings.Contains(response.Body.String(), `"password"`) || len(calls) != before {
			t.Fatal("opted-out or unprepared member received credentials", response.Body.String())
		}
	}
	a.store.Data.Devices[1].Layer2 = Layer2Status{Enabled: true, Prepared: true}
	a.store.Data.Devices[0].Layer2.Enabled = false
	if response := request(a.public(), "POST", "/agent/layer2", nil, token); response.Code != 409 {
		t.Fatal("disabled entry was usable", response.Body.String())
	}
	a.store.Data.Devices[0].Layer2.Enabled = true
	var plan map[string]any
	json.Unmarshal(w.Body.Bytes(), &plan)
	if plan["role"] != "member" || plan["password"] != a.cfg.Layer2.credential(&a.store.Data.Devices[1]) {
		t.Fatal("wrong device credential")
	}
	public, _ := json.Marshal(publicDevice(a.store.Data.Devices[1]))
	if strings.Contains(string(public), plan["password"].(string)) {
		t.Fatal("device credential leaked in inventory")
	}
	for _, state := range []string{"paused", "disabled"} {
		a.store.Data.Devices[1].State = state
		if r := request(a.public(), "POST", "/agent/layer2", nil, token); r.Code != 403 {
			t.Fatal("inactive device authorized", state, r.Code)
		}
	}
	a.store.Data.Devices[1].State = "active"
	a.store.Data.Devices[1].LastSeen = time.Now().Add(-time.Minute)
	if r := request(a.public(), "POST", "/agent/layer2", nil, token); r.Code != 403 {
		t.Fatal("stale member authorized")
	}
	a.store.Data.Devices[1].LastSeen = time.Now()
	a.store.Data.Devices[0].Network.BridgeEligible = false
	if r := request(a.public(), "POST", "/agent/layer2", nil, token); r.Code != 409 {
		t.Fatal("wireless entry authorized")
	}
	calls = nil
	if e := a.cfg.Layer2.revoke(&a.store.Data.Devices[1], false); e != nil {
		t.Fatal(e)
	}
	if strings.Join(calls, ",") != "SetUser,EnumSession,DeleteSession" {
		t.Fatal(calls)
	}
}

func TestLayer2ModeGuardsAndHeartbeat(t *testing.T) {
	a := testApp(t)
	attempt := func(want int) {
		t.Helper()
		w := request(http.HandlerFunc(a.networkMode), "POST", "/", map[string]string{"mode": "bridged"}, "")
		if w.Code != want {
			t.Fatal(w.Code, w.Body.String())
		}
	}
	attempt(409)
	a.cfg.Layer2 = testLayer2(t, nil)
	attempt(409)
	token := secret()
	a.store.Data.EntryID = "entry"
	a.store.Data.Devices = []Device{{ID: "entry", State: "active", Connected: true, LastSeen: time.Now(), TokenHash: digest(token), LANIP: "192.168.20.2", Layer2: Layer2Status{Enabled: true, Prepared: true}, Network: NetworkReport{BridgeEligible: true}}}
	a.store.Data.Mappings = []Mapping{{ID: "old"}}
	attempt(409)
	a.store.Data.Mappings = nil
	a.store.Data.Devices[0].Layer2.Enabled = false
	attempt(409)
	a.store.Data.Devices[0].Layer2.Enabled = true
	attempt(200)
	w := request(http.HandlerFunc(a.addMapping), "POST", "/", map[string]any{"name": "app", "deviceId": "entry", "port": 8080}, "")
	if w.Code != 409 {
		t.Fatal("mapping allowed in bridge mode", w.Code)
	}
	w = request(a.public(), "POST", "/agent/heartbeat", map[string]any{"lanIp": "172.18.0.1", "networks": []string{"172.18.0.0/30"}, "network": map[string]any{"kind": "tunnel", "adapterId": "fake", "bridgeEligible": true}}, token)
	if w.Code != 200 {
		t.Fatal(w.Body.String())
	}
	d := a.store.Data.Devices[0]
	if d.LANIP != "" || len(d.Networks) != 0 || d.Network.BridgeEligible {
		t.Fatal("TUN became LAN entry", d)
	}
}

func TestRouteCleanupIdempotent(t *testing.T) {
	s := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(404) }))
	defer s.Close()
	b := Backend{URL: s.URL}
	if e := b.removeRoute("already-removed"); e != nil {
		t.Fatal(e)
	}
}

func TestLayer2OptOutRevokesOnlyDependants(t *testing.T) {
	a := testApp(t)
	var revoked []string
	a.cfg.Layer2 = testLayer2(t, func(method string, p map[string]any) any {
		if method == "SetUser" {
			if p["policy:Access_bool"] != false {
				t.Error("revocation enabled access")
			}
			revoked = append(revoked, p["Name_str"].(string))
		}
		return map[string]any{}
	})
	a.store.Data.NetworkMode = "bridged"
	a.store.Data.EntryID = "entry"
	for _, id := range []string{"entry", "member"} {
		a.store.Data.Devices = append(a.store.Data.Devices, Device{ID: id, State: "active", Connected: true, LastSeen: time.Now(), Network: NetworkReport{BridgeEligible: true}, Layer2: Layer2Status{Enabled: true, Prepared: true}})
	}
	a.store.Data.Devices[1].Layer2.Enabled = false
	a.revokeExpiredLayer2()
	if strings.Join(revoked, ",") != layer2User("member") {
		t.Fatal("member opt-out revoked another device", revoked)
	}
	revoked = nil
	a.store.Data.Devices[1].Layer2.Enabled = true
	a.store.Data.Devices[0].Layer2.Enabled = false
	a.revokeExpiredLayer2()
	if len(revoked) != 2 {
		t.Fatal("entry opt-out left dependant authorization", revoked)
	}
}
