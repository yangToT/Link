package main

import (
	"net/http"
	"net/url"
	"time"
)

// Called with the store locked. Keep the record until upstream cleanup succeeds,
// so an interrupted deletion can be retried using the same ownership information.
func (a *App) deleteDevice(w http.ResponseWriter, d *Device, self string) {
	if d.ID == self {
		failure(w, 409, "不能删除当前管理设备，请从其他管理员设备操作")
		return
	}
	if d.GroupID == "" {
		failure(w, 409, "设备网络归属缺失，不能安全删除")
		return
	}
	entry := a.store.Data.EntryID == d.ID
	if a.cfg.Layer2 != nil {
		if entry {
			for i := range a.store.Data.Devices {
				peer := &a.store.Data.Devices[i]
				if err := a.cfg.Layer2.revoke(peer, peer.ID == d.ID); err != nil {
					failure(w, 503, "二层连接撤销未完成，保留设备以便重试")
					return
				}
			}
		} else if err := a.cfg.Layer2.revoke(d, false); err != nil {
			failure(w, 503, "二层连接撤销未完成，保留设备以便重试")
			return
		}
		if err := a.cfg.Layer2.rpc("DeleteUser", map[string]any{"HubName_str": a.cfg.Layer2.Hub, "Name_str": layer2User(d.ID)}, nil); err != nil {
			failure(w, 503, "二层设备清理失败，请重试")
			return
		}
	}
	if entry {
		for _, id := range a.store.Data.RouteIDs {
			if a.backend.removeRoute(id) != nil {
				failure(w, 502, "入口路由清理失败，请重试")
				return
			}
		}
	}
	if a.backend.revokeGroup(d.GroupID) != nil {
		failure(w, 502, "网络访问撤销未完成，保留设备以便重试")
		return
	}
	if a.backend.call("DELETE", "/api/groups/"+url.PathEscape(d.GroupID), nil, nil) != nil {
		failure(w, 502, "网络分组清理失败，请重试")
		return
	}
	old := a.store.Data
	devices := []Device{}
	mappings := []Mapping{}
	for _, item := range old.Devices {
		if item.ID != d.ID {
			devices = append(devices, item)
		}
	}
	for _, item := range old.Mappings {
		if item.DeviceID != d.ID {
			mappings = append(mappings, item)
		}
	}
	a.store.Data.Devices = devices
	a.store.Data.Mappings = mappings
	if entry {
		a.store.Data.EntryID = ""
		a.store.Data.RouteIDs = nil
		a.store.Data.NetworkMode = "routed"
	}
	a.store.event("删除设备", d.Name)
	if a.store.persist() != nil {
		a.store.Data = old
		failure(w, 500, "保存失败，设备访问已撤销，请重试删除")
		return
	}
	writeJSON(w, 200, map[string]bool{"ok": true})
}

// Voluntary departure retains the peer so routed entries keep their route references.
// The authorized client stops its local transport; administrator kick/delete still revoke peers.
func (a *App) disconnect(w http.ResponseWriter, r *http.Request) {
	a.store.Lock()
	defer a.store.Unlock()
	d, ok := a.deviceAuth(r, true)
	if !ok {
		failure(w, 403, "设备未获授权")
		return
	}
	if a.cfg.Layer2 != nil && a.cfg.Layer2.revoke(d, d.ID == a.store.Data.EntryID) != nil {
		failure(w, 503, "二层访问撤销未完成")
		return
	}
	old := *d
	d.Connected = false
	d.LastSeen = time.Time{}
	d.Layer2.State = "off"
	d.Layer2.IP = ""
	if a.store.persist() != nil {
		*d = old
		failure(w, 500, "保存断开状态失败")
		return
	}
	writeJSON(w, 200, map[string]bool{"ok": true})
}
