package auth

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/hex"
	"encoding/pem"
	"fmt"
	"math/big"
	"net"
	"os"
	"path/filepath"
	"slices"
	"syscall"
	"time"
)

const tlsRenewalWindow = 30 * 24 * time.Hour

// LoadOrCreateTLS loads the probe identity or issues a replacement when it
// cannot serve for the next 30 days.
func LoadOrCreateTLS(stateDir, nodeID string) (*tls.Config, string, error) {
	if stateDir == "" {
		return nil, "", fmt.Errorf("TLS state directory is required")
	}
	stateDir = filepath.Clean(stateDir)
	if filepath.Dir(stateDir) == stateDir && filepath.IsAbs(stateDir) {
		return nil, "", fmt.Errorf("filesystem root cannot be used as the TLS state directory")
	}
	if err := privateDirectory(stateDir); err != nil {
		return nil, "", err
	}
	dir := filepath.Join(stateDir, "tls")
	if err := privateDirectory(dir); err != nil {
		return nil, "", err
	}

	lock, err := os.OpenFile(filepath.Join(dir, ".lock"), os.O_CREATE|os.O_RDWR|syscall.O_NOFOLLOW, 0600)
	if err != nil {
		return nil, "", err
	}
	defer lock.Close()
	for {
		err = syscall.Flock(int(lock.Fd()), syscall.LOCK_EX)
		if err != syscall.EINTR {
			break
		}
	}
	if err != nil {
		return nil, "", fmt.Errorf("lock TLS state: %w", err)
	}

	certPath, keyPath := filepath.Join(dir, "cert.pem"), filepath.Join(dir, "key.pem")
	names := tlsNames(nodeID)
	certOK, err := setRegularFileMode(certPath, 0644)
	if err != nil {
		return nil, "", err
	}
	keyOK, err := setRegularFileMode(keyPath, 0600)
	if err != nil {
		return nil, "", err
	}
	now := time.Now()
	if certOK && keyOK {
		cert, err := tls.LoadX509KeyPair(certPath, keyPath)
		if err == nil && reusableCertificate(cert, names, now) {
			return configForCertificate(cert)
		}
	}

	cert, err := generateCertificate(names, nodeID, now)
	if err != nil {
		return nil, "", err
	}
	keyDER, err := x509.MarshalECPrivateKey(cert.PrivateKey.(*ecdsa.PrivateKey))
	if err != nil {
		return nil, "", err
	}
	keyTemp, err := stagePEM(dir, "EC PRIVATE KEY", keyDER, 0600)
	if err != nil {
		return nil, "", err
	}
	defer os.Remove(keyTemp)
	certTemp, err := stagePEM(dir, "CERTIFICATE", cert.Certificate[0], 0644)
	if err != nil {
		return nil, "", err
	}
	defer os.Remove(certTemp)
	if err := os.Rename(keyTemp, keyPath); err != nil {
		return nil, "", err
	}
	if err := os.Rename(certTemp, certPath); err != nil {
		return nil, "", err
	}
	tlsDir, err := os.Open(dir)
	if err != nil {
		return nil, "", err
	}
	defer tlsDir.Close()
	if err := tlsDir.Sync(); err != nil {
		return nil, "", err
	}
	return configForCertificate(cert)
}

// NewEphemeralTLS creates a temporary identity for probes without writable state.
func NewEphemeralTLS(nodeID string) (*tls.Config, string, error) {
	cert, err := generateCertificate(tlsNames(nodeID), nodeID, time.Now())
	if err != nil {
		return nil, "", err
	}
	return configForCertificate(cert)
}

// TLSFingerprint returns the lowercase SHA-256 digest of the configured leaf DER.
func TLSFingerprint(cfg *tls.Config) string {
	if cfg == nil || len(cfg.Certificates) == 0 || len(cfg.Certificates[0].Certificate) == 0 {
		return ""
	}
	digest := sha256.Sum256(cfg.Certificates[0].Certificate[0])
	return hex.EncodeToString(digest[:])
}

func configForCertificate(cert tls.Certificate) (*tls.Config, string, error) {
	cfg := &tls.Config{Certificates: []tls.Certificate{cert}, MinVersion: tls.VersionTLS13}
	return cfg, TLSFingerprint(cfg), nil
}

func tlsNames(nodeID string) []string {
	names := []string{"localhost"}
	hostname, _ := os.Hostname()
	for _, name := range []string{nodeID, nodeID + ".local", hostname} {
		if name != "" && name != ".local" && !slices.Contains(names, name) {
			names = append(names, name)
		}
	}
	return names
}

func generateCertificate(names []string, nodeID string, now time.Time) (tls.Certificate, error) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return tls.Certificate{}, err
	}
	serial, err := rand.Int(rand.Reader, new(big.Int).Lsh(big.NewInt(1), 128))
	if err != nil {
		return tls.Certificate{}, err
	}
	cert := &x509.Certificate{
		SerialNumber: serial.SetBit(serial, 127, 1),
		Subject:      pkix.Name{CommonName: nodeID},
		NotBefore:    now.Add(-time.Minute),
		NotAfter:     now.Add(366 * 24 * time.Hour),
		KeyUsage:     x509.KeyUsageDigitalSignature,
		ExtKeyUsage:  []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
		IPAddresses:  []net.IP{net.IPv4(127, 0, 0, 1), net.IPv6loopback},
	}
	for _, name := range names {
		if ip := net.ParseIP(name); ip != nil {
			cert.IPAddresses = append(cert.IPAddresses, ip)
		} else {
			cert.DNSNames = append(cert.DNSNames, name)
		}
	}
	der, err := x509.CreateCertificate(rand.Reader, cert, cert, &key.PublicKey, key)
	if err != nil {
		return tls.Certificate{}, err
	}
	return tls.Certificate{Certificate: [][]byte{der}, PrivateKey: key}, nil
}

func reusableCertificate(cert tls.Certificate, names []string, now time.Time) bool {
	key, ok := cert.PrivateKey.(*ecdsa.PrivateKey)
	if !ok || key.Curve != elliptic.P256() {
		return false
	}
	leaf := cert.Leaf
	if leaf == nil {
		var err error
		leaf, err = x509.ParseCertificate(cert.Certificate[0])
		if err != nil {
			return false
		}
	}
	if now.Before(leaf.NotBefore) || leaf.NotAfter.Before(now.Add(tlsRenewalWindow)) {
		return false
	}
	if leaf.CheckSignature(leaf.SignatureAlgorithm, leaf.RawTBSCertificate, leaf.Signature) != nil {
		return false
	}
	roots := x509.NewCertPool()
	roots.AddCert(leaf)
	if _, err := leaf.Verify(x509.VerifyOptions{Roots: roots, CurrentTime: now}); err != nil {
		return false
	}
	for _, name := range names {
		if leaf.VerifyHostname(name) != nil {
			return false
		}
	}
	return true
}

func privateDirectory(path string) error {
	if err := os.MkdirAll(path, 0700); err != nil {
		return err
	}
	info, err := os.Lstat(path)
	if err != nil {
		return err
	}
	if !info.IsDir() {
		return fmt.Errorf("TLS state path %q must be a directory", path)
	}
	if info.Mode().Perm() != 0700 {
		return os.Chmod(path, 0700)
	}
	return nil
}

func setRegularFileMode(path string, mode os.FileMode) (bool, error) {
	info, err := os.Lstat(path)
	if os.IsNotExist(err) {
		return false, nil
	}
	if err != nil {
		return false, err
	}
	if !info.Mode().IsRegular() {
		return false, nil
	}
	if info.Mode().Perm() != mode {
		return true, os.Chmod(path, mode)
	}
	return true, nil
}

func stagePEM(dir, blockType string, der []byte, mode os.FileMode) (string, error) {
	file, err := os.CreateTemp(dir, ".pem-*")
	if err != nil {
		return "", err
	}
	name := file.Name()
	defer func() {
		file.Close()
		if err != nil {
			os.Remove(name)
		}
	}()
	if err = file.Chmod(mode); err == nil {
		err = pem.Encode(file, &pem.Block{Type: blockType, Bytes: der})
	}
	if err == nil {
		err = file.Sync()
	}
	if err == nil {
		err = file.Close()
	}
	return name, err
}
