package main

import (
	"context"
	"crypto/sha256"
	"crypto/tls"
	"embed"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"encoding/pem"
	"flag"
	"fmt"
	"io/fs"
	"log"
	"net"
	"net/http"
	"net/http/httputil"
	"net/url"
	"os"
	"os/signal"
	"path/filepath"
	"strings"
	"sync"
	"syscall"
	"time"
)

//go:embed web/index.html web/app.js web/style.css
var assets embed.FS

var version = "0.2.0-alpha.1"

type Session struct {
	Device  string
	CSRF    string
	Expires time.Time
}
type App struct {
	events    *eventHub
	cfg       Config
	store     *Store
	backend   *Backend
	ca        []byte
	pin       string
	sessionMu sync.Mutex
	sessions  map[string]Session
	tickets   map[string]Session
	limitMu   sync.Mutex
	limits    map[string][]time.Time
}

func newApp(cfg Config, s *Store, ca []byte) *App {
	p, _ := pem.Decode(ca)
	var pin string
	if p != nil {
		h := sha256.Sum256(p.Bytes)
		pin = hex.EncodeToString(h[:])
	}
	events := newEventHub()
	s.onChange = events.notify
	return &App{events: events, cfg: cfg, store: s, backend: &Backend{URL: cfg.BackendURL, Token: cfg.BackendToken}, ca: ca, pin: pin, sessions: map[string]Session{}, tickets: map[string]Session{}, limits: map[string][]time.Time{}}
}
func main() {
	dir := flag.String("data", "./data", "instance directory")
	command := flag.String("command", "serve", "serve, init, join, certificate")
	host := flag.String("public-host", "", "public IP for init")
	role := flag.String("role", "admin", "join role: admin or member")
	hosts := flag.String("hosts", "", "certificate SANs, comma-separated")
	flag.Parse()
	if *command == "init" {
		if e := initInstance(*dir, *host); e != nil {
			log.Fatal(e)
		}
		fmt.Println("Instance initialized; configuration and certificates saved.")
		return
	}
	if *command == "certificate" {
		if e := issueLeaf(*dir, strings.Split(*hosts, ",")); e != nil {
			log.Fatal(e)
		}
		fmt.Println("Certificate updated.")
		return
	}
	cfgBytes, e := os.ReadFile(filepath.Join(*dir, "config.json"))
	if e != nil {
		log.Fatal("cannot read instance config")
	}
	var cfg Config
	if json.Unmarshal(cfgBytes, &cfg) != nil {
		log.Fatal("invalid instance config")
	}
	store, e := openStore(filepath.Join(*dir, "state.json"))
	if e != nil {
		log.Fatal("cannot read state")
	}
	ca, e := os.ReadFile(filepath.Join(*dir, "ca.pem"))
	if e != nil {
		log.Fatal("cannot read instance certificate")
	}
	app := newApp(cfg, store, ca)
	if *command == "join" {
		if *role != "admin" && *role != "member" {
			log.Fatal("invalid role")
		}
		code, e := app.queueJoin(*role)
		if e != nil {
			log.Fatal(e)
		}
		if e = atomicWrite(filepath.Join(*dir, "join-code.txt"), []byte(code+"\n"), 0600); e != nil {
			log.Fatal(e)
		}
		fmt.Println("Join credential written to instance join-code.txt; valid for 15 minutes.")
		return
	}
	if *command != "serve" {
		log.Fatal("unknown command")
	}
	if cfg.PublicListen == cfg.PrivateListen {
		log.Fatal("public and private listeners must differ")
	}
	privateHost := hostOnly(cfg.PrivateListen)
	ip := net.ParseIP(privateHost)
	if ip == nil || ip.IsUnspecified() {
		log.Fatal("private listener must bind an explicit loopback or overlay address")
	}
	cert := filepath.Join(*dir, "server.pem")
	key := filepath.Join(*dir, "server.key")
	pub := &http.Server{Addr: cfg.PublicListen, Handler: app.public(), ReadHeaderTimeout: 10 * time.Second, IdleTimeout: 90 * time.Second, MaxHeaderBytes: 16384, TLSConfig: &tls.Config{MinVersion: tls.VersionTLS12}}
	priv := &http.Server{Addr: cfg.PrivateListen, Handler: app.private(), ReadHeaderTimeout: 10 * time.Second, IdleTimeout: 60 * time.Second, MaxHeaderBytes: 16384, TLSConfig: &tls.Config{MinVersion: tls.VersionTLS12}}
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()
	go app.reconcile(ctx)
	for _, srv := range []*http.Server{pub, priv} {
		go func(s *http.Server) {
			if e := s.ListenAndServeTLS(cert, key); e != nil && e != http.ErrServerClosed {
				log.Fatal("listener failed: ", e)
			}
		}(srv)
	}
	log.Print("Link started; web assets are embedded; private management listener enabled")
	<-ctx.Done()
	close(app.events.done)
	stop, c := context.WithTimeout(context.Background(), 10*time.Second)
	defer c()
	_ = pub.Shutdown(stop)
	_ = priv.Shutdown(stop)
}
func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}
func failure(w http.ResponseWriter, status int, message string) {
	writeJSON(w, status, map[string]string{"error": message})
}
func body(w http.ResponseWriter, r *http.Request, v any) bool {
	r.Body = http.MaxBytesReader(w, r.Body, 65536)
	dec := json.NewDecoder(r.Body)
	dec.DisallowUnknownFields()
	if dec.Decode(v) != nil {
		failure(w, 400, "请求格式不正确")
		return false
	}
	return true
}
func (a *App) createJoin(role string) (string, error) {
	key := secret()
	a.store.Lock()
	defer a.store.Unlock()
	now := time.Now()
	kept := a.store.Data.Joins[:0]
	for _, j := range a.store.Data.Joins {
		if j.Expires.After(now) {
			kept = append(kept, j)
		}
	}
	a.store.Data.Joins = append(kept, Join{digest(key), role, now.Add(15 * time.Minute)})
	if e := a.store.persist(); e != nil {
		return "", e
	}
	return "LINK1." + a.pin + "." + key, nil
}

// Recovery commands use a spool; they never rewrite a running server's state file.
func (a *App) queueJoin(role string) (string, error) {
	key := secret()
	join := Join{digest(key), role, time.Now().Add(15 * time.Minute)}
	b, _ := json.Marshal(join)
	path := filepath.Join(filepath.Dir(a.store.path), "join-requests", secret()+".json")
	if err := atomicWrite(path, b, 0600); err != nil {
		return "", err
	}
	return "LINK1." + a.pin + "." + key, nil
}
func (a *App) consumeJoins() {
	files, _ := filepath.Glob(filepath.Join(filepath.Dir(a.store.path), "join-requests", "*.json"))
	a.store.Lock()
	defer a.store.Unlock()
	for _, path := range files {
		var join Join
		b, err := os.ReadFile(path)
		if err != nil || json.Unmarshal(b, &join) != nil {
			continue
		}
		if !join.Expires.After(time.Now()) {
			_ = os.Remove(path)
			continue
		}
		found := false
		for _, existing := range a.store.Data.Joins {
			if existing.Hash == join.Hash {
				found = true
			}
		}
		if !found {
			a.store.Data.Joins = append(a.store.Data.Joins, join)
		}
		if a.store.persist() == nil {
			_ = os.Remove(path)
		}
	}
}
func (a *App) public() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("GET /health", func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, 200, map[string]string{"service": "Link", "status": "running", "version": version})
	})
	mux.HandleFunc("GET /bootstrap/ca", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/x-pem-file")
		_, _ = w.Write(a.ca)
	})
	mux.HandleFunc("POST /agent/enroll", a.enroll)
	mux.HandleFunc("POST /agent/reconnect", a.reconnect)
	mux.HandleFunc("POST /agent/heartbeat", a.heartbeat)
	mux.HandleFunc("GET /agent/events", a.agentEvents)
	mux.HandleFunc("POST /agent/browser-ticket", a.browserTicket)
	mux.HandleFunc("POST /agent/layer2", a.layer2Plan)
	target, e := url.Parse(a.cfg.BackendURL)
	if e == nil && target.Host != "" {
		proxy := httputil.NewSingleHostReverseProxy(target)
		h2 := httputil.NewSingleHostReverseProxy(target)
		protocols := new(http.Protocols)
		protocols.SetUnencryptedHTTP2(true)
		h2.Transport = &http.Transport{Protocols: protocols}
		proxy.ErrorHandler = func(w http.ResponseWriter, r *http.Request, e error) { failure(w, 502, "网络组件暂不可用") }
		h2.ErrorHandler = proxy.ErrorHandler
		for _, path := range []string{"/management.ManagementService/", "/signalexchange.SignalExchange/"} {
			mux.Handle(path, h2)
		}
		for _, path := range []string{"/relay", "/relay/", "/ws-proxy/"} {
			mux.Handle(path, proxy)
		}
	}
	mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) { http.NotFound(w, r) })
	return mux
}
func (a *App) limited(r *http.Request) bool {
	a.limitMu.Lock()
	defer a.limitMu.Unlock()
	now := time.Now()
	key := hostOnly(r.RemoteAddr)
	list := a.limits[key]
	active := list[:0]
	for _, t := range list {
		if now.Sub(t) < time.Minute {
			active = append(active, t)
		}
	}
	if len(active) >= 12 {
		return true
	}
	a.limits[key] = append(active, now)
	if len(a.limits) > 10000 {
		for k, v := range a.limits {
			if len(v) == 0 || now.Sub(v[len(v)-1]) > time.Minute {
				delete(a.limits, k)
			}
		}
	}
	return false
}
func (a *App) deviceAuth(r *http.Request, allowPaused bool) (*Device, bool) {
	token := strings.TrimPrefix(r.Header.Get("Authorization"), "Bearer ")
	if len(token) < 32 {
		return nil, false
	}
	for i := range a.store.Data.Devices {
		d := &a.store.Data.Devices[i]
		if d.TokenHash == digest(token) && d.State != "disabled" && (allowPaused || d.State == "active") {
			return d, true
		}
	}
	return nil, false
}
func (a *App) enroll(w http.ResponseWriter, r *http.Request) {
	if a.limited(r) {
		failure(w, 429, "请求过于频繁")
		return
	}
	var req struct {
		Code string `json:"code"`
		Name string `json:"name"`
		OS   string `json:"os"`
	}
	if !body(w, r, &req) {
		return
	}
	if len(req.Name) < 1 || len(req.Name) > 64 || len(req.OS) > 100 {
		failure(w, 400, "设备名称不正确")
		return
	}
	parts := strings.Split(req.Code, ".")
	if len(parts) != 3 || parts[0] != "LINK1" || parts[1] != a.pin {
		failure(w, 401, "加入凭据无效或已过期")
		return
	}
	a.store.Lock()
	defer a.store.Unlock()
	idx := -1
	role := ""
	for i, j := range a.store.Data.Joins {
		if j.Hash == digest(parts[2]) && j.Expires.After(time.Now()) {
			idx = i
			role = j.Role
			break
		}
	}
	if idx < 0 {
		failure(w, 401, "加入凭据无效或已过期")
		return
	}
	id := secret()[:20]
	group, e := a.backend.group("link-" + id)
	if e != nil {
		failure(w, 503, "网络组件暂不可用，请稍后重试")
		return
	}
	key, e := a.backend.setupKey(group)
	if e != nil {
		failure(w, 503, "无法生成网络凭据")
		return
	}
	token := secret()
	d := Device{ID: id, Name: req.Name, OS: req.OS, Role: role, State: "active", TokenHash: digest(token), GroupID: group, Networks: []string{}, MappingStates: map[string]string{}}
	a.store.Data.Joins = append(a.store.Data.Joins[:idx], a.store.Data.Joins[idx+1:]...)
	a.store.Data.Devices = append(a.store.Data.Devices, d)
	a.store.event("设备已注册", d.Name)
	if a.store.persist() != nil {
		failure(w, 500, "无法保存注册状态")
		return
	}
	writeJSON(w, 201, map[string]any{"deviceId": id, "token": token, "setupKey": key, "managementUrl": a.cfg.PublicURL, "role": role})
}
func (a *App) reconnect(w http.ResponseWriter, r *http.Request) {
	a.store.Lock()
	defer a.store.Unlock()
	d, ok := a.deviceAuth(r, true)
	if !ok {
		failure(w, 403, "设备未获授权")
		return
	}
	if d.State == "active" && d.PeerID != "" {
		writeJSON(w, 200, map[string]string{"setupKey": "", "managementUrl": a.cfg.PublicURL})
		return
	}
	key, e := a.backend.setupKey(d.GroupID)
	if e != nil {
		failure(w, 503, "无法生成网络凭据")
		return
	}
	d.State = "active"
	if a.store.persist() != nil {
		failure(w, 500, "保存失败")
		return
	}
	writeJSON(w, 200, map[string]string{"setupKey": key, "managementUrl": a.cfg.PublicURL})
}
func (a *App) heartbeat(w http.ResponseWriter, r *http.Request) {
	var req struct {
		Networks      []string          `json:"networks"`
		LANIP         string            `json:"lanIp"`
		MappingStates map[string]string `json:"mappingStates"`
		Applications  []Application     `json:"applications"`
		Network       NetworkReport     `json:"network"`
		Layer2        Layer2Status      `json:"layer2"`
	}
	if !body(w, r, &req) {
		return
	}
	a.store.Lock()
	defer a.store.Unlock()
	d, ok := a.deviceAuth(r, true)
	if !ok {
		failure(w, 403, "设备已停用或凭据无效")
		return
	}
	if d.State != "active" {
		writeJSON(w, 200, a.agentState(d))
		return
	}
	d.LastSeen = time.Now().UTC()
	d.Network = cleanNetworkReport(req.Network)
	d.Layer2 = cleanLayer2Status(req.Layer2)
	oldLAN := d.LANIP
	d.LANIP = ""
	if privateIP(req.LANIP) && d.Network.AdapterID != "" && (d.Network.Kind == "ethernet" || d.Network.Kind == "wifi") {
		d.LANIP = req.LANIP
	}
	if oldLAN != d.LANIP && a.store.Data.EntryID == d.ID {
		for _, id := range a.store.Data.RouteIDs {
			if a.backend.removeRoute(id) != nil {
				d.LANIP = oldLAN
				failure(w, 503, "入口变更后的旧路由尚未撤销")
				return
			}
		}
		a.store.Data.RouteIDs = nil
	}
	d.Networks = []string{}
	for _, n := range req.Networks {
		if d.LANIP != "" && validNetwork(n) && len(d.Networks) < 16 {
			d.Networks = append(d.Networks, n)
		}
	}
	if len(req.MappingStates) <= 100 {
		d.MappingStates = req.MappingStates
	}
	d.Applications = []Application{}
	for _, app := range req.Applications {
		if app.Port > 0 && app.Port <= 65535 && len(app.Name) <= 100 && len(d.Applications) < 100 {
			d.Applications = append(d.Applications, app)
		}
	}
	a.events.notify()
	writeJSON(w, 200, a.agentState(d))
}
func (a *App) browserTicket(w http.ResponseWriter, r *http.Request) {
	a.store.Lock()
	d, ok := a.deviceAuth(r, false)
	if !ok || d.Role != "admin" || !d.Connected || time.Since(d.LastSeen) > 35*time.Second {
		a.store.Unlock()
		failure(w, 403, "请通过已连接的管理员设备打开管理中心")
		return
	}
	id := d.ID
	a.store.Unlock()
	token := secret()
	a.sessionMu.Lock()
	a.tickets[digest(token)] = Session{Device: id, Expires: time.Now().Add(30 * time.Second)}
	a.sessionMu.Unlock()
	writeJSON(w, 200, map[string]string{"url": a.cfg.PrivateURL + "/#ticket=" + url.QueryEscape(token)})
}
func (a *App) private() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("POST /session", a.login)
	mux.HandleFunc("/api/", a.adminAPI)
	files, _ := fs.Sub(assets, "web")
	mux.Handle("/", http.FileServer(http.FS(files)))
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Security-Policy", "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'")
		w.Header().Set("X-Content-Type-Options", "nosniff")
		w.Header().Set("Referrer-Policy", "no-referrer")
		w.Header().Set("Cache-Control", "no-store")
		if r.Method != "GET" && r.Method != "HEAD" {
			origin := r.Header.Get("Origin")
			if origin != "https://"+r.Host {
				failure(w, 403, "跨来源请求被拒绝")
				return
			}
		}
		mux.ServeHTTP(w, r)
	})
}
func (a *App) login(w http.ResponseWriter, r *http.Request) {
	var req struct {
		Ticket string `json:"ticket"`
	}
	if !body(w, r, &req) {
		return
	}
	a.sessionMu.Lock()
	t, ok := a.tickets[digest(req.Ticket)]
	delete(a.tickets, digest(req.Ticket))
	a.sessionMu.Unlock()
	if !ok || time.Now().After(t.Expires) {
		failure(w, 401, "登录凭据已过期，请从客户端重新打开")
		return
	}
	a.store.Lock()
	d := a.store.device(t.Device)
	allowed := d != nil && d.State == "active" && d.Role == "admin" && d.IP == hostOnly(r.RemoteAddr)
	a.store.Unlock()
	if !allowed {
		failure(w, 403, "请从对应的已连接设备打开")
		return
	}
	token := secret()
	t.CSRF = secret()
	t.Expires = time.Now().Add(8 * time.Hour)
	a.sessionMu.Lock()
	a.sessions[digest(token)] = t
	a.sessionMu.Unlock()
	http.SetCookie(w, &http.Cookie{Name: "__Host-LinkSession", Value: token, Path: "/", Secure: true, HttpOnly: true, SameSite: http.SameSiteStrictMode, MaxAge: 28800})
	writeJSON(w, 200, map[string]string{"csrf": t.CSRF})
}
func (a *App) session(r *http.Request) (Session, bool) {
	c, e := r.Cookie("__Host-LinkSession")
	if e != nil {
		return Session{}, false
	}
	a.sessionMu.Lock()
	s, ok := a.sessions[digest(c.Value)]
	a.sessionMu.Unlock()
	if !ok || time.Now().After(s.Expires) {
		return s, false
	}
	a.store.Lock()
	d := a.store.device(s.Device)
	allowed := d != nil && d.State == "active" && d.Role == "admin" && d.IP == hostOnly(r.RemoteAddr) && time.Since(d.LastSeen) < 35*time.Second
	a.store.Unlock()
	return s, allowed
}
func (a *App) adminAPI(w http.ResponseWriter, r *http.Request) {
	session, ok := a.session(r)
	if !ok {
		failure(w, 401, "请从客户端打开管理中心")
		return
	}
	if r.Method != "GET" && r.Header.Get("X-Link-CSRF") != session.CSRF {
		failure(w, 403, "请求校验失败")
		return
	}
	if r.Method == "GET" && r.URL.Path == "/api/events" {
		a.adminEvents(w, r, session)
		return
	}
	if r.Method == "GET" && r.URL.Path == "/api/state" {
		a.store.Lock()
		defer a.store.Unlock()
		writeJSON(w, 200, a.adminState(session.CSRF))
		return
	}
	if r.Method == "POST" && r.URL.Path == "/api/network-mode" {
		a.networkMode(w, r)
		return
	}
	if r.Method == "POST" && r.URL.Path == "/api/joins" {
		var req struct {
			Role string `json:"role"`
		}
		if !body(w, r, &req) {
			return
		}
		if req.Role != "member" && req.Role != "admin" {
			failure(w, 400, "请选择成员角色")
			return
		}
		code, e := a.createJoin(req.Role)
		if e != nil {
			failure(w, 500, "生成失败")
			return
		}
		writeJSON(w, 201, map[string]string{"code": code, "server": a.cfg.PublicURL})
		return
	}
	if r.Method == "POST" && r.URL.Path == "/api/device" {
		a.deviceAction(w, r, session.Device)
		return
	}
	if r.Method == "POST" && r.URL.Path == "/api/mappings" {
		a.addMapping(w, r)
		return
	}
	if r.Method == "DELETE" && strings.HasPrefix(r.URL.Path, "/api/mappings/") {
		id := strings.TrimPrefix(r.URL.Path, "/api/mappings/")
		a.store.Lock()
		defer a.store.Unlock()
		for i, m := range a.store.Data.Mappings {
			if m.ID == id {
				a.store.Data.Mappings = append(a.store.Data.Mappings[:i], a.store.Data.Mappings[i+1:]...)
				a.store.event("移除服务映射", m.Name)
				if a.store.persist() != nil {
					failure(w, 500, "保存失败")
					return
				}
				writeJSON(w, 200, map[string]bool{"ok": true})
				return
			}
		}
		failure(w, 404, "映射不存在")
		return
	}
	http.NotFound(w, r)
}
func (a *App) deviceAction(w http.ResponseWriter, r *http.Request, self string) {
	var req struct {
		ID     string `json:"id"`
		Action string `json:"action"`
	}
	if !body(w, r, &req) {
		return
	}
	a.store.Lock()
	defer a.store.Unlock()
	d := a.store.device(req.ID)
	if d == nil {
		failure(w, 404, "设备不存在")
		return
	}
	switch req.Action {
	case "entry":
		if !d.Connected || d.State != "active" || d.PeerID == "" || len(d.Networks) == 0 {
			failure(w, 409, "设备需要在线并报告可用的本地网络")
			return
		}
		if a.store.Data.NetworkMode == "bridged" {
			if !d.Network.BridgeEligible {
				failure(w, 409, "二层入口必须使用可用的有线物理网卡")
				return
			}
			if a.store.Data.EntryID != d.ID {
				for i := range a.store.Data.Devices {
					peer := &a.store.Data.Devices[i]
					if a.cfg.Layer2.revoke(peer, peer.ID == a.store.Data.EntryID) != nil {
						failure(w, 503, "旧二层连接尚未撤销")
						return
					}
				}
			}
			a.store.Data.EntryID = d.ID
			break
		}
		created := []string{}
		for _, network := range d.Networks {
			id, e := a.backend.route(d, network, a.cfg.AllGroup)
			if e != nil {
				for _, rid := range created {
					_ = a.backend.removeRoute(rid)
				}
				failure(w, 502, "入口配置失败，原入口保持不变")
				return
			}
			created = append(created, id)
		}
		for _, rid := range a.store.Data.RouteIDs {
			if e := a.backend.removeRoute(rid); e != nil {
				for _, id := range created {
					_ = a.backend.removeRoute(id)
				}
				failure(w, 502, "旧路由清理失败，请重试")
				return
			}
		}
		a.store.Data.EntryID = d.ID
		a.store.Data.RouteIDs = created
	case "kick", "disable":
		if d.ID == self {
			failure(w, 409, "不能断开当前管理设备，请从其他管理员设备操作")
			return
		}
		if a.store.Data.NetworkMode == "bridged" && a.cfg.Layer2.revoke(d, d.ID == a.store.Data.EntryID) != nil {
			failure(w, 503, "二层访问撤销未完成")
			return
		}
		if e := a.backend.revokeGroup(d.GroupID); e != nil {
			failure(w, 502, "网络撤销未完成，设备状态未变更")
			return
		}
		d.PeerID = ""
		d.Connected = false
		d.State = "paused"
		if req.Action == "disable" {
			d.State = "disabled"
		}
	case "enable":
		d.State = "paused"
	default:
		failure(w, 400, "不支持的操作")
		return
	}
	a.store.event(req.Action, d.Name)
	if a.store.persist() != nil {
		failure(w, 500, "保存失败")
		return
	}
	writeJSON(w, 200, map[string]bool{"ok": true})
}
func (a *App) addMapping(w http.ResponseWriter, r *http.Request) {
	var req struct {
		Name     string `json:"name"`
		DeviceID string `json:"deviceId"`
		Port     int    `json:"port"`
	}
	if !body(w, r, &req) {
		return
	}
	if len(req.Name) < 1 || len(req.Name) > 64 || req.Port < 1 || req.Port > 65535 {
		failure(w, 400, "名称或服务端口无效")
		return
	}
	a.store.Lock()
	defer a.store.Unlock()
	if a.store.Data.NetworkMode == "bridged" {
		failure(w, 409, "二层模式使用设备的局域网地址，无需端口映射")
		return
	}
	d := a.store.device(req.DeviceID)
	entry := a.store.device(a.store.Data.EntryID)
	if len(a.store.Data.Mappings) >= 100 {
		failure(w, 409, "当前版本最多支持 100 条映射")
		return
	}
	if d == nil || d.State != "active" || entry == nil {
		failure(w, 409, "请先配置可用设备与网络入口")
		return
	}
	port := 22000
	used := map[int]bool{}
	for _, app := range entry.Applications {
		used[app.Port] = true
	}
	for _, m := range a.store.Data.Mappings {
		used[m.EntryPort] = true
		if m.Name == req.Name {
			failure(w, 409, "服务名称已存在")
			return
		}
	}
	for used[port] {
		port++
	}
	if port > 22999 {
		failure(w, 409, "当前映射数量已达上限")
		return
	}
	m := Mapping{ID: secret()[:20], Name: req.Name, DeviceID: req.DeviceID, Port: req.Port, EntryPort: port, State: "pending"}
	a.store.Data.Mappings = append(a.store.Data.Mappings, m)
	a.store.event("添加服务映射", m.Name)
	if a.store.persist() != nil {
		failure(w, 500, "保存失败")
		return
	}
	writeJSON(w, 201, m)
}
func (a *App) reconcile(ctx context.Context) {
	ticker := time.NewTicker(5 * time.Second)
	defer ticker.Stop()
	for {
		a.consumeJoins()
		a.sessionMu.Lock()
		for key, s := range a.sessions {
			if time.Now().After(s.Expires) {
				delete(a.sessions, key)
			}
		}
		for key, s := range a.tickets {
			if time.Now().After(s.Expires) {
				delete(a.tickets, key)
			}
		}
		a.sessionMu.Unlock()
		a.syncPeers()
		a.revokeExpiredLayer2()
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		}
	}
}
func (a *App) syncPeers() {
	peers, e := a.backend.peers()
	if e != nil {
		return
	}
	a.store.Lock()
	defer a.store.Unlock()
	for i := range a.store.Data.Devices {
		d := &a.store.Data.Devices[i]
		d.Connected = false
		for _, p := range peers {
			for _, g := range p.Groups {
				if g.ID == d.GroupID && d.State != "active" {
					_ = a.backend.removePeer(p.ID)
					continue
				}
				if g.ID == d.GroupID && d.State == "active" {
					d.PeerID = p.ID
					d.IP = p.IP
					d.Connected = p.Connected
				}
			}
		}
	}
	entry := a.store.device(a.store.Data.EntryID)
	for i := range a.store.Data.Mappings {
		m := &a.store.Data.Mappings[i]
		m.State = "pending"
		target := a.store.device(m.DeviceID)
		if entry == nil || !publicDevice(*entry).Connected || target == nil || !publicDevice(*target).Connected {
			m.State = "offline"
			continue
		}
		if status, ok := entry.MappingStates[m.ID]; ok && status == "ready" {
			m.State = "ready"
		} else if ok && status == "error" {
			m.State = "error"
		}
	}
	if e := a.store.persist(); e != nil {
		log.Print("state persistence failed")
	}
}

// Included in enrollment bundles without storing raw device secrets server-side.
func certificateBundle(b []byte) string { return base64.StdEncoding.EncodeToString(b) }
