package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"time"
)

type Peer struct {
	ID        string `json:"id"`
	Name      string `json:"name"`
	IP        string `json:"ip"`
	Connected bool   `json:"connected"`
	Groups    []struct {
		ID string `json:"id"`
	} `json:"groups"`
}
type Backend struct {
	URL   string
	Token string
	HTTP  *http.Client
}

func (b *Backend) call(method, path string, body any, out any) error {
	var data []byte
	var e error
	if body != nil {
		data, e = json.Marshal(body)
		if e != nil {
			return e
		}
	}
	req, e := http.NewRequest(method, b.URL+path, bytes.NewReader(data))
	if e != nil {
		return e
	}
	req.Header.Set("Authorization", "Token "+b.Token)
	req.Header.Set("Content-Type", "application/json")
	client := b.HTTP
	if client == nil {
		client = &http.Client{Timeout: 12 * time.Second}
	}
	resp, e := client.Do(req)
	if e != nil {
		return fmt.Errorf("network backend unavailable")
	}
	defer resp.Body.Close()
	if resp.StatusCode >= 300 {
		io.Copy(io.Discard, io.LimitReader(resp.Body, 4096))
		return fmt.Errorf("network backend returned %d", resp.StatusCode)
	}
	if out != nil {
		return json.NewDecoder(io.LimitReader(resp.Body, 4<<20)).Decode(out)
	}
	return nil
}
func (b *Backend) group(name string) (string, error) {
	var r struct {
		ID string `json:"id"`
	}
	e := b.call("POST", "/api/groups", map[string]any{"name": name, "peers": []string{}}, &r)
	return r.ID, e
}
func (b *Backend) setupKey(group string) (string, error) {
	var r struct {
		Key string `json:"key"`
	}
	e := b.call("POST", "/api/setup-keys", map[string]any{"name": "Link device", "type": "one-off", "expires_in": 900, "auto_groups": []string{group}, "usage_limit": 1, "ephemeral": false}, &r)
	return r.Key, e
}
func (b *Backend) peers() ([]Peer, error) {
	var p []Peer
	e := b.call("GET", "/api/peers", nil, &p)
	return p, e
}
func (b *Backend) removePeer(id string) error {
	if id == "" {
		return nil
	}
	return b.call("DELETE", "/api/peers/"+url.PathEscape(id), nil, nil)
}
func (b *Backend) revokeGroup(group string) error {
	var keys []struct {
		ID     string   `json:"id"`
		Groups []string `json:"auto_groups"`
	}
	if err := b.call("GET", "/api/setup-keys", nil, &keys); err != nil {
		return err
	}
	for _, key := range keys {
		for _, id := range key.Groups {
			if id == group {
				if err := b.call("DELETE", "/api/setup-keys/"+url.PathEscape(key.ID), nil, nil); err != nil {
					return err
				}
				break
			}
		}
	}
	peers, err := b.peers()
	if err != nil {
		return err
	}
	for _, peer := range peers {
		for _, g := range peer.Groups {
			if g.ID == group {
				if err := b.removePeer(peer.ID); err != nil {
					return err
				}
				break
			}
		}
	}
	return nil
}
func (b *Backend) route(device *Device, network, group string) (string, error) {
	var r struct {
		ID string `json:"id"`
	}
	e := b.call("POST", "/api/routes", map[string]any{"description": "Link managed resource", "network_id": "link-" + device.ID[:8] + "-" + digest(network)[:8], "network": network, "peer": device.PeerID, "groups": []string{group}, "access_control_groups": []string{group}, "enabled": true, "masquerade": true, "metric": 9999}, &r)
	return r.ID, e
}
func (b *Backend) removeRoute(id string) error {
	return b.call("DELETE", "/api/routes/"+url.PathEscape(id), nil, nil)
}
