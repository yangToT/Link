package main

import (
	"bytes"
	"crypto/sha256"
	"encoding/json"
	"fmt"
	"net/http"
	"sync"
	"time"
)

// A notification coalesces pending changes. Consumers always receive current state,
// so reconnects need no unbounded event history or replay of old commands.
type eventHub struct {
	sync.Mutex
	done     chan struct{}
	changed  chan struct{}
	revision uint64
	epoch    string
	clients  map[string]int
}

func newEventHub() *eventHub {
	return &eventHub{done: make(chan struct{}), changed: make(chan struct{}), epoch: secret()[:16], clients: map[string]int{}}
}
func (h *eventHub) notify() {
	h.Lock()
	close(h.changed)
	h.changed = make(chan struct{})
	h.revision++
	h.Unlock()
}
func (h *eventHub) stamp(v map[string]any) {
	h.Lock()
	v["revision"] = h.revision
	v["streamEpoch"] = h.epoch
	h.Unlock()
}
func (h *eventHub) watch() <-chan struct{} { h.Lock(); defer h.Unlock(); return h.changed }
func (h *eventHub) acquire(key string) bool {
	h.Lock()
	defer h.Unlock()
	if h.clients[key] >= 4 {
		return false
	}
	h.clients[key]++
	return true
}
func (h *eventHub) release(key string) {
	h.Lock()
	defer h.Unlock()
	h.clients[key]--
	if h.clients[key] == 0 {
		delete(h.clients, key)
	}
}

// Caller holds store lock. The HTTP heartbeat and stream share exactly one config view.
func (a *App) agentState(d *Device) map[string]any {
	if d.State != "active" {
		v := map[string]any{"action": "disconnect"}
		a.events.stamp(v)
		return v
	}
	devices := []Device{}
	for _, p := range a.store.Data.Devices {
		devices = append(devices, publicDevice(p))
	}
	forwards := []map[string]any{}
	if a.store.Data.NetworkMode != "bridged" && a.store.Data.EntryID == d.ID && privateIP(d.LANIP) {
		for _, m := range a.store.Data.Mappings {
			target := a.store.device(m.DeviceID)
			if target != nil && target.State == "active" && target.IP != "" {
				forwards = append(forwards, map[string]any{"id": m.ID, "listenIp": d.LANIP, "listenPort": m.EntryPort, "targetIp": target.IP, "targetPort": m.Port})
			}
		}
	}
	v := map[string]any{"action": "keep", "device": publicDevice(*d), "devices": devices, "entryId": a.store.Data.EntryID, "forwards": forwards, "mappings": a.store.Data.Mappings, "privateUrl": a.cfg.PrivateURL, "networkMode": a.store.Data.NetworkMode}
	a.events.stamp(v)
	return v
}
func (a *App) adminState(csrf string) map[string]any {
	devices := []Device{}
	for _, d := range a.store.Data.Devices {
		devices = append(devices, publicDevice(d))
	}
	v := map[string]any{"devices": devices, "entryId": a.store.Data.EntryID, "mappings": a.store.Data.Mappings, "events": a.store.Data.Events, "csrf": csrf, "networkMode": a.store.Data.NetworkMode, "layer2Configured": a.cfg.Layer2.validate() == nil}
	a.events.stamp(v)
	return v
}
func freezeState(v map[string]any) map[string]any {
	b, _ := json.Marshal(v)
	decoder := json.NewDecoder(bytes.NewReader(b))
	decoder.UseNumber()
	var result map[string]any
	_ = decoder.Decode(&result)
	return result
}
func stateSignature(v map[string]any) [32]byte {
	copy := freezeState(v)
	delete(copy, "revision")
	if list, ok := copy["devices"].([]any); ok {
		for _, value := range list {
			if d, ok := value.(map[string]any); ok {
				delete(d, "lastSeen")
			}
		}
	}
	if d, ok := copy["device"].(map[string]any); ok {
		delete(d, "lastSeen")
	}
	b, _ := json.Marshal(copy)
	return sha256.Sum256(b)
}
func (a *App) stream(w http.ResponseWriter, r *http.Request, key string, read func() (map[string]any, bool)) {
	if !a.events.acquire(key) {
		failure(w, 429, "实时连接过多")
		return
	}
	defer a.events.release(key)
	// Register before reading so a concurrent change cannot be lost between them.
	changed := a.events.watch()
	state, ok := read()
	if !ok {
		failure(w, 403, "连接授权已失效")
		return
	}
	if _, ok := w.(http.Flusher); !ok {
		failure(w, 500, "实时响应不受支持")
		return
	}
	w.Header().Set("Content-Type", "text/event-stream; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("X-Accel-Buffering", "no")
	control := http.NewResponseController(w)
	send := func(event string, v any) error {
		_ = control.SetWriteDeadline(time.Now().Add(10 * time.Second))
		defer control.SetWriteDeadline(time.Time{})
		b, e := json.Marshal(v)
		if e != nil {
			return e
		}
		if _, e = fmt.Fprintf(w, "event: %s\ndata: %s\n\n", event, b); e != nil {
			return e
		}
		return control.Flush()
	}
	last := stateSignature(state)
	if send("state", state) != nil {
		return
	}
	if state["action"] == "disconnect" {
		return
	}
	keepalive := time.NewTicker(15 * time.Second)
	defer keepalive.Stop()
	for {
		ping := false
		select {
		case <-a.events.done:
			return
		case <-r.Context().Done():
			return
		case <-changed:
		case <-keepalive.C:
			ping = true
		}
		changed = a.events.watch()
		state, ok = read()
		if !ok {
			_ = send("revoked", map[string]string{"action": "disconnect"})
			return
		}
		signature := stateSignature(state)
		if signature != last {
			if send("state", state) != nil {
				return
			}
			last = signature
			if state["action"] == "disconnect" {
				return
			}
		} else if ping {
			_ = control.SetWriteDeadline(time.Now().Add(10 * time.Second))
			if _, e := fmt.Fprint(w, ": keepalive\n\n"); e != nil {
				return
			}
			if control.Flush() != nil {
				return
			}
			_ = control.SetWriteDeadline(time.Time{})
		}
	}
}
func (a *App) agentEvents(w http.ResponseWriter, r *http.Request) {
	a.store.Lock()
	d, ok := a.deviceAuth(r, true)
	key := ""
	if ok {
		key = "agent:" + d.ID
	}
	a.store.Unlock()
	if !ok {
		failure(w, 403, "设备未获授权")
		return
	}
	a.stream(w, r, key, func() (map[string]any, bool) {
		a.store.Lock()
		defer a.store.Unlock()
		d, ok := a.deviceAuth(r, true)
		if !ok {
			return nil, false
		}
		return freezeState(a.agentState(d)), true
	})
}
func (a *App) adminEvents(w http.ResponseWriter, r *http.Request, s Session) {
	a.stream(w, r, "admin:"+s.Device, func() (map[string]any, bool) {
		current, ok := a.session(r)
		if !ok {
			return nil, false
		}
		a.store.Lock()
		defer a.store.Unlock()
		d := a.store.device(current.Device)
		if d == nil || d.State != "active" || d.Role != "admin" || d.IP != hostOnly(r.RemoteAddr) || time.Since(d.LastSeen) >= 35*time.Second {
			return nil, false
		}
		return freezeState(a.adminState(current.CSRF)), true
	})
}
