package main

import (
	"bytes"
	"crypto/hmac"
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"encoding/hex"
	"encoding/json"
	"encoding/pem"
	"errors"
	"io"
	"net"
	"net/http"
	"net/netip"
	"net/url"
	"regexp"
	"strconv"
	"strings"
	"time"
)

// The optional SoftEther hub is reached through the existing private overlay.
// Its management secret never leaves the server. No public management listener is added.
type Layer2Config struct {
	Endpoint    string `json:"endpoint"`
	APIURL      string `json:"apiUrl"`
	Password    string `json:"password"`
	Hub         string `json:"hub"`
	Certificate string `json:"certificate"`
}
type NetworkReport struct {
	AdapterID      string `json:"adapterId"`
	AdapterName    string `json:"adapterName"`
	Kind           string `json:"kind"`
	Gateway        string `json:"gateway"`
	BridgeEligible bool   `json:"bridgeEligible"`
	TunDetected    bool   `json:"tunDetected"`
	Warning        string `json:"warning"`
}
type Layer2Status struct {
	State   string `json:"state"`
	Message string `json:"message"`
	IP      string `json:"ip"`
}

var safeHub = regexp.MustCompile(`^[A-Za-z0-9_-]{1,40}$`)

func (c *Layer2Config) validate() error {
	if c == nil {
		return errors.New("二层组件尚未配置")
	}
	host, port, e := net.SplitHostPort(c.Endpoint)
	n, pe := strconv.Atoi(port)
	if e != nil || pe != nil || n < 1 || n > 65535 {
		return errors.New("二层端点无效")
	}
	ip, e := netip.ParseAddr(host)
	overlay := netip.MustParsePrefix("100.64.0.0/10")
	if e != nil || !overlay.Contains(ip) || !safeHub.MatchString(c.Hub) || len(c.Password) < 20 {
		return errors.New("二层组件必须使用私有端点和独立管理凭据")
	}
	u, e := url.Parse(c.APIURL)
	if e != nil || u.Scheme != "https" || u.User != nil || u.RawQuery != "" || u.Fragment != "" || u.Path != "/api/" {
		return errors.New("二层管理地址无效")
	}
	adminIP := net.ParseIP(u.Hostname())
	if adminIP == nil || !adminIP.IsLoopback() {
		return errors.New("二层管理接口必须使用本机地址")
	}
	block, _ := pem.Decode([]byte(c.Certificate))
	if block == nil || block.Type != "CERTIFICATE" {
		return errors.New("缺少二层服务端证书")
	}
	if _, e := x509.ParseCertificate(block.Bytes); e != nil {
		return errors.New("二层服务端证书无效")
	}
	return nil
}
func (c *Layer2Config) rpc(method string, params map[string]any, out any) error {
	if e := c.validate(); e != nil {
		return e
	}
	block, _ := pem.Decode([]byte(c.Certificate))
	pin := sha256.Sum256(block.Bytes)
	transport := &http.Transport{Proxy: nil, TLSClientConfig: &tls.Config{MinVersion: tls.VersionTLS12, InsecureSkipVerify: true, // Exact certificate pin replaces public-PKI validation for the local management endpoint.
		VerifyConnection: func(s tls.ConnectionState) error {
			if len(s.PeerCertificates) == 0 {
				return errors.New("missing certificate")
			}
			actual := sha256.Sum256(s.PeerCertificates[0].Raw)
			if actual != pin {
				return errors.New("certificate pin mismatch")
			}
			return nil
		}}}
	defer transport.CloseIdleConnections()
	body, _ := json.Marshal(map[string]any{"jsonrpc": "2.0", "id": "link", "method": method, "params": params})
	r, _ := http.NewRequest("POST", c.APIURL, bytes.NewReader(body))
	r.Header.Set("Content-Type", "application/json")
	r.Header.Set("X-VPNADMIN-PASSWORD", c.Password)
	client := &http.Client{Transport: transport, Timeout: 8 * time.Second, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
	response, e := client.Do(r)
	if e != nil {
		return errors.New("二层管理组件无法连接")
	}
	defer response.Body.Close()
	if response.StatusCode != 200 {
		return errors.New("二层管理组件拒绝请求")
	}
	var result struct {
		Result json.RawMessage `json:"result"`
		Error  json.RawMessage `json:"error"`
	}
	if json.NewDecoder(io.LimitReader(response.Body, 1<<20)).Decode(&result) != nil {
		return errors.New("二层组件响应无效")
	}
	if len(result.Error) > 0 && string(result.Error) != "null" {
		return errors.New("二层组件操作失败")
	}
	if len(result.Result) == 0 {
		return errors.New("二层组件缺少结果")
	}
	if out != nil {
		return json.Unmarshal(result.Result, out)
	}
	return nil
}
func layer2User(id string) string {
	sum := sha256.Sum256([]byte(id))
	return "link-" + hex.EncodeToString(sum[:12])
}
func (c *Layer2Config) credential(d *Device) string {
	mac := hmac.New(sha256.New, []byte(c.Password))
	mac.Write([]byte(d.ID + ":" + d.TokenHash))
	return hex.EncodeToString(mac.Sum(nil))
}
func (c *Layer2Config) user(d *Device, entry bool, access bool) error {
	if e := c.validate(); e != nil {
		return e
	}
	p := map[string]any{"HubName_str": c.Hub, "Name_str": layer2User(d.ID), "AuthType_u32": 1, "Auth_Password_str": c.credential(d), "UsePolicy_bool": true, "policy:Access_bool": access, "policy:DHCPNoServer_bool": !entry, "policy:NoBridge_bool": !entry, "policy:NoRouting_bool": !entry, "policy:FilterIPv6_bool": true, "policy:FixPassword_bool": true, "policy:AutoDisconnect_u32": 0, "policy:Ver3_bool": true, "policy:MultiLogins_u32": 1, "ExpireTime_dt": time.Now().UTC().Add(90 * time.Second).Format("2006-01-02T15:04:05.000")}
	if e := c.rpc("SetUser", p, nil); e != nil {
		return c.rpc("CreateUser", p, nil)
	}
	return nil
}
func (c *Layer2Config) revoke(d *Device, entry bool) error {
	if e := c.user(d, entry, false); e != nil {
		return e
	}
	var sessions struct {
		Items []struct {
			Name string `json:"Name_str"`
			User string `json:"Username_str"`
		} `json:"SessionList"`
	}
	if e := c.rpc("EnumSession", map[string]any{"HubName_str": c.Hub}, &sessions); e != nil {
		return e
	}
	for _, s := range sessions.Items {
		if strings.EqualFold(s.User, layer2User(d.ID)) {
			if e := c.rpc("DeleteSession", map[string]any{"HubName_str": c.Hub, "Name_str": s.Name}, nil); e != nil {
				return e
			}
		}
	}
	return nil
}
func (a *App) layer2Plan(w http.ResponseWriter, r *http.Request) {
	a.store.Lock()
	defer a.store.Unlock()
	d, ok := a.deviceAuth(r, false)
	if !ok || !publicDevice(*d).Connected {
		failure(w, 403, "设备未获授权")
		return
	}
	if a.store.Data.NetworkMode != "bridged" {
		writeJSON(w, 200, map[string]any{"enabled": false})
		return
	}
	entry := a.store.device(a.store.Data.EntryID)
	if entry == nil || !publicDevice(*entry).Connected || !entry.Network.BridgeEligible || !privateIP(entry.LANIP) {
		failure(w, 409, "有线入口不可用")
		return
	}
	if e := a.cfg.Layer2.validate(); e != nil {
		failure(w, 503, e.Error())
		return
	}
	isEntry := entry.ID == d.ID
	if e := a.cfg.Layer2.user(d, isEntry, true); e != nil {
		failure(w, 503, "二层设备授权失败")
		return
	}
	writeJSON(w, 200, map[string]any{"enabled": true, "role": map[bool]string{true: "entry", false: "member"}[isEntry], "endpoint": a.cfg.Layer2.Endpoint, "hub": a.cfg.Layer2.Hub, "username": layer2User(d.ID), "password": a.cfg.Layer2.credential(d), "certificate": a.cfg.Layer2.Certificate, "entryId": entry.ID, "entryIp": entry.LANIP, "gateway": entry.Network.Gateway, "networks": entry.Networks})
}
func (a *App) networkMode(w http.ResponseWriter, r *http.Request) {
	var req struct {
		Mode string `json:"mode"`
	}
	if !body(w, r, &req) {
		return
	}
	if req.Mode != "routed" && req.Mode != "bridged" {
		failure(w, 400, "无效接入模式")
		return
	}
	a.store.Lock()
	defer a.store.Unlock()
	if req.Mode == a.store.Data.NetworkMode {
		writeJSON(w, 200, map[string]bool{"ok": true})
		return
	}
	if req.Mode == "bridged" {
		if e := a.cfg.Layer2.validate(); e != nil {
			failure(w, 409, e.Error())
			return
		}
		entry := a.store.device(a.store.Data.EntryID)
		if entry == nil || !publicDevice(*entry).Connected || !entry.Network.BridgeEligible {
			failure(w, 409, "请先选择可用的有线入口")
			return
		}
		if len(a.store.Data.Mappings) > 0 {
			failure(w, 409, "请先移除端口映射，再切换二层模式")
			return
		}
		if e := a.cfg.Layer2.rpc("GetHub", map[string]any{"HubName_str": a.cfg.Layer2.Hub}, nil); e != nil {
			failure(w, 503, "二层交换组件尚未就绪")
			return
		}
	}
	if a.store.Data.NetworkMode == "bridged" {
		for i := range a.store.Data.Devices {
			if e := a.cfg.Layer2.revoke(&a.store.Data.Devices[i], a.store.Data.Devices[i].ID == a.store.Data.EntryID); e != nil {
				failure(w, 503, "二层连接撤销失败，模式未切换")
				return
			}
		}
	}
	for _, rid := range a.store.Data.RouteIDs {
		if e := a.backend.removeRoute(rid); e != nil {
			failure(w, 502, "旧路由撤销失败，模式未切换")
			return
		}
	}
	a.store.Data.RouteIDs = nil
	a.store.Data.NetworkMode = req.Mode
	// Returning to routed mode requires selecting the entry again; never silently recreate old routes.
	if req.Mode == "routed" {
		a.store.Data.EntryID = ""
	}
	a.store.event("接入模式", req.Mode)
	if a.store.persist() != nil {
		failure(w, 500, "保存失败")
		return
	}
	writeJSON(w, 200, map[string]bool{"ok": true})
}
func cleanNetworkReport(n NetworkReport) NetworkReport {
	if len(n.AdapterID) > 100 || len(n.AdapterName) > 160 || len(n.Warning) > 300 {
		return NetworkReport{Warning: "网卡报告无效"}
	}
	n.BridgeEligible = n.BridgeEligible && n.Kind == "ethernet" && n.AdapterID != "" && privateIP(n.Gateway)
	if n.Kind != "ethernet" && n.Kind != "wifi" {
		n = NetworkReport{TunDetected: n.TunDetected, Warning: "没有可用物理入口"}
	}
	return n
}
func cleanLayer2Status(s Layer2Status) Layer2Status {
	switch s.State {
	case "off", "preparing", "waiting-address", "attached", "entry-ready", "blocked", "cleanup-failed":
	default:
		s.State = "blocked"
	}
	if len(s.Message) > 300 {
		s.Message = "二层状态报告无效"
	}
	if !privateIP(s.IP) {
		s.IP = ""
	}
	return s
}
func (a *App) revokeExpiredLayer2() {
	a.store.Lock()
	defer a.store.Unlock()
	if a.store.Data.NetworkMode != "bridged" || a.cfg.Layer2 == nil {
		return
	}
	entry := a.store.device(a.store.Data.EntryID)
	entryOK := entry != nil && publicDevice(*entry).Connected && entry.Network.BridgeEligible
	for i := range a.store.Data.Devices {
		d := &a.store.Data.Devices[i]
		if !entryOK || !publicDevice(*d).Connected {
			if e := a.cfg.Layer2.revoke(d, d.ID == a.store.Data.EntryID); e != nil {
				d.Layer2 = Layer2Status{State: "blocked", Message: "二层撤销待重试"}
				return
			}
		}
	}
}
