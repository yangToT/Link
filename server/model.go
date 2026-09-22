package main

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"net"
	"net/netip"
	"os"
	"path/filepath"
	"sync"
	"time"
)

type Config struct {
	PublicURL     string `json:"publicUrl"`
	PublicListen  string `json:"publicListen"`
	PrivateListen string `json:"privateListen"`
	PrivateURL    string `json:"privateUrl"`
	BackendURL    string `json:"backendUrl"`
	BackendToken  string `json:"backendToken"`
	AllGroup      string `json:"allGroup"`
}
type Device struct {
	ID            string            `json:"id"`
	Name          string            `json:"name"`
	OS            string            `json:"os"`
	Role          string            `json:"role"`
	State         string            `json:"state"`
	TokenHash     string            `json:"tokenHash,omitempty"`
	GroupID       string            `json:"groupId,omitempty"`
	PeerID        string            `json:"peerId,omitempty"`
	IP            string            `json:"ip"`
	LANIP         string            `json:"lanIp,omitempty"`
	Networks      []string          `json:"networks"`
	LastSeen      time.Time         `json:"lastSeen"`
	Connected     bool              `json:"connected"`
	MappingStates map[string]string `json:"mappingStates,omitempty"`
	Applications  []Application     `json:"applications"`
}
type Application struct {
	Name string `json:"name"`
	Port int    `json:"port"`
}
type Join struct {
	Hash    string    `json:"hash"`
	Role    string    `json:"role"`
	Expires time.Time `json:"expires"`
}
type Mapping struct {
	ID        string `json:"id"`
	Name      string `json:"name"`
	DeviceID  string `json:"deviceId"`
	Port      int    `json:"port"`
	EntryPort int    `json:"entryPort"`
	State     string `json:"state"`
}
type Event struct {
	Time   time.Time `json:"time"`
	Action string    `json:"action"`
	Device string    `json:"device"`
}
type State struct {
	Devices  []Device  `json:"devices"`
	Joins    []Join    `json:"joins"`
	EntryID  string    `json:"entryId"`
	RouteIDs []string  `json:"routeIds"`
	Mappings []Mapping `json:"mappings"`
	Events   []Event   `json:"events"`
}
type Store struct {
	sync.Mutex
	path string
	Data State
}

func openStore(path string) (*Store, error) {
	s := &Store{path: path, Data: State{Devices: []Device{}, Joins: []Join{}, Mappings: []Mapping{}, Events: []Event{}}}
	b, e := os.ReadFile(path)
	if errors.Is(e, os.ErrNotExist) {
		return s, nil
	}
	if e != nil {
		return nil, e
	}
	e = json.Unmarshal(b, &s.Data)
	return s, e
}

// Call with Store locked. Atomic rename prevents partial state after a crash.
func (s *Store) persist() error {
	b, e := json.MarshalIndent(s.Data, "", "  ")
	if e != nil {
		return e
	}
	return atomicWrite(s.path, b, 0600)
}
func atomicWrite(path string, b []byte, mode os.FileMode) error {
	if e := os.MkdirAll(filepath.Dir(path), 0700); e != nil {
		return e
	}
	f, e := os.CreateTemp(filepath.Dir(path), ".link-*")
	if e != nil {
		return e
	}
	name := f.Name()
	defer os.Remove(name)
	if e = f.Chmod(mode); e == nil {
		_, e = f.Write(b)
	}
	if e == nil {
		e = f.Sync()
	}
	ce := f.Close()
	if e != nil {
		return e
	}
	if ce != nil {
		return ce
	}
	return os.Rename(name, path)
}
func secret() string {
	b := make([]byte, 32)
	if _, e := rand.Read(b); e != nil {
		panic(e)
	}
	return base64.RawURLEncoding.EncodeToString(b)
}
func digest(s string) string { b := sha256.Sum256([]byte(s)); return hex.EncodeToString(b[:]) }
func (s *Store) event(action, device string) {
	s.Data.Events = append([]Event{{time.Now().UTC(), action, device}}, s.Data.Events...)
	if len(s.Data.Events) > 500 {
		s.Data.Events = s.Data.Events[:500]
	}
}
func (s *Store) device(id string) *Device {
	for i := range s.Data.Devices {
		if s.Data.Devices[i].ID == id {
			return &s.Data.Devices[i]
		}
	}
	return nil
}
func publicDevice(d Device) Device {
	d.TokenHash = ""
	d.GroupID = ""
	d.PeerID = ""
	d.Connected = d.Connected && d.State == "active" && time.Since(d.LastSeen) < 35*time.Second
	return d
}
func privateIP(s string) bool {
	ip, err := netip.ParseAddr(s)
	return err == nil && ip.Is4() && ip.IsPrivate()
}
func validNetwork(s string) bool {
	p, e := netip.ParsePrefix(s)
	return e == nil && p.Addr().Is4() && p.Addr().IsPrivate() && p.Bits() >= 16 && p.Bits() <= 32 && p == p.Masked()
}
func hostOnly(s string) string {
	h, _, err := net.SplitHostPort(s)
	if err == nil {
		return h
	}
	return s
}
