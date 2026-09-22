package main

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/json"
	"encoding/pem"
	"fmt"
	"math/big"
	"net"
	"os"
	"path/filepath"
	"time"
)

func serial() *big.Int {
	n, e := rand.Int(rand.Reader, new(big.Int).Lsh(big.NewInt(1), 128))
	if e != nil {
		panic(e)
	}
	return n
}
func initInstance(dir, publicHost string) error {
	if net.ParseIP(publicHost) == nil {
		return fmt.Errorf("public-host must be a literal IP")
	}
	if _, e := os.Stat(filepath.Join(dir, "config.json")); !os.IsNotExist(e) {
		return fmt.Errorf("instance already exists")
	}
	if e := os.MkdirAll(dir, 0700); e != nil {
		return e
	}
	key, e := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if e != nil {
		return e
	}
	now := time.Now()
	tmpl := &x509.Certificate{SerialNumber: serial(), Subject: pkix.Name{CommonName: "Link instance CA"}, NotBefore: now.Add(-time.Hour), NotAfter: now.AddDate(5, 0, 0), IsCA: true, BasicConstraintsValid: true, KeyUsage: x509.KeyUsageCertSign | x509.KeyUsageCRLSign}
	der, e := x509.CreateCertificate(rand.Reader, tmpl, tmpl, &key.PublicKey, key)
	if e != nil {
		return e
	}
	kb, e := x509.MarshalECPrivateKey(key)
	if e != nil {
		return e
	}
	for name, blob := range map[string][]byte{"ca.pem": pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}), "ca.key": pem.EncodeToMemory(&pem.Block{Type: "EC PRIVATE KEY", Bytes: kb})} {
		if e := atomicWrite(filepath.Join(dir, name), blob, 0600); e != nil {
			return e
		}
	}
	if e := issueLeaf(dir, []string{publicHost, "127.0.0.1"}); e != nil {
		return e
	}
	cfg := Config{PublicURL: "https://" + net.JoinHostPort(publicHost, "24443"), PublicListen: ":24443", PrivateListen: "127.0.0.1:24444", PrivateURL: "https://127.0.0.1:24444", BackendURL: "http://127.0.0.1:24445"}
	b, _ := json.MarshalIndent(cfg, "", "  ")
	if e := atomicWrite(filepath.Join(dir, "config.json"), b, 0600); e != nil {
		return e
	}
	return nil
}
func issueLeaf(dir string, hosts []string) error {
	cb, e := os.ReadFile(filepath.Join(dir, "ca.pem"))
	if e != nil {
		return e
	}
	cp, _ := pem.Decode(cb)
	if cp == nil {
		return fmt.Errorf("invalid CA")
	}
	ca, e := x509.ParseCertificate(cp.Bytes)
	if e != nil {
		return e
	}
	kb, e := os.ReadFile(filepath.Join(dir, "ca.key"))
	if e != nil {
		return e
	}
	kp, _ := pem.Decode(kb)
	if kp == nil {
		return fmt.Errorf("invalid CA key")
	}
	cak, e := x509.ParseECPrivateKey(kp.Bytes)
	if e != nil {
		return e
	}
	key, e := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if e != nil {
		return e
	}
	now := time.Now()
	tmpl := &x509.Certificate{SerialNumber: serial(), Subject: pkix.Name{CommonName: "Link"}, NotBefore: now.Add(-time.Hour), NotAfter: now.AddDate(1, 0, 0), KeyUsage: x509.KeyUsageDigitalSignature, ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth}, BasicConstraintsValid: true}
	for _, h := range hosts {
		if ip := net.ParseIP(h); ip != nil {
			tmpl.IPAddresses = append(tmpl.IPAddresses, ip)
		} else {
			tmpl.DNSNames = append(tmpl.DNSNames, h)
		}
	}
	der, e := x509.CreateCertificate(rand.Reader, tmpl, ca, &key.PublicKey, cak)
	if e != nil {
		return e
	}
	priv, _ := x509.MarshalECPrivateKey(key)
	if e := atomicWrite(filepath.Join(dir, "server.pem"), append(pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}), cb...), 0600); e != nil {
		return e
	}
	return atomicWrite(filepath.Join(dir, "server.key"), pem.EncodeToMemory(&pem.Block{Type: "EC PRIVATE KEY", Bytes: priv}), 0600)
}
