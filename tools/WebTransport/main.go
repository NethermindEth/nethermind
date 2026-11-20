// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

package main

import (
	"bufio"
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"net/netip"
	"os"
	"sync"
	"sync/atomic"
	"time"

	"github.com/quic-go/quic-go"
	"github.com/quic-go/quic-go/http3"
	"github.com/quic-go/webtransport-go"
)

const (
	maxConnections      = 64
	maxConnectionsPerIP = 8
	maxSessions         = 16
	maxSessionsPerIP    = 4
	connectionTimeout   = 30 * time.Second
	sessionTimeout      = 10 * time.Second
	certificateAge      = 14 * 24 * time.Hour
	rotationInterval    = 7 * 24 * time.Hour
)

type config struct {
	Listen  string
	Backend string
	Token   string
}

type advertisement struct {
	Address string `json:"address"`
	Hashes  string `json:"hashes"`
}

func main() {
	if err := run(os.Stdin, os.Stdout); err != nil {
		log.Fatal(err)
	}
}

func run(input io.Reader, output io.Writer) error {
	// Keep the token out of command-line arguments and process listings.
	reader := bufio.NewReader(input)
	line, err := reader.ReadSlice('\n')
	if err != nil {
		return fmt.Errorf("read startup config: %w", err)
	}
	var cfg config
	if err := json.Unmarshal(line, &cfg); err != nil {
		return fmt.Errorf("decode startup config: %w", err)
	}
	backend, err := netip.ParseAddrPort(cfg.Backend)
	if err != nil || !backend.Addr().IsLoopback() || backend.Port() == 0 {
		return errors.New("backend must be a loopback address with a nonzero port")
	}
	token, err := hex.DecodeString(cfg.Token)
	if err != nil || len(token) != 32 {
		return errors.New("invalid backend token")
	}
	addr, err := netip.ParseAddrPort(cfg.Listen)
	if err != nil {
		return fmt.Errorf("listen must be an IP endpoint: %w", err)
	}

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	go func() {
		_, _ = io.Copy(io.Discard, reader)
		cancel()
	}()
	return listen(ctx, addr, backend, [32]byte(token), output)
}

func listen(ctx context.Context, addr, backend netip.AddrPort, token [32]byte, output io.Writer) error {
	addr = netip.AddrPortFrom(addr.Addr().Unmap(), addr.Port())
	network := "udp"
	switch {
	case addr.Addr().Is4():
		network = "udp4"
	case addr.Addr() != netip.IPv6Unspecified():
		network = "udp6"
	}
	socket, err := net.ListenUDP(network, net.UDPAddrFromAddrPort(addr))
	if err != nil {
		return err
	}
	defer socket.Close()

	current, next, err := certificatePair(time.Now())
	if err != nil {
		return err
	}
	var cert atomic.Pointer[tls.Certificate]
	cert.Store(current)
	quicConfig := &quic.Config{
		EnableDatagrams:                  true,
		EnableStreamResetPartialDelivery: true,
		MaxIdleTimeout:                   sessionTimeout,
		MaxIncomingStreams:               4,
		MaxIncomingUniStreams:            4,
		MaxStreamReceiveWindow:           512 << 10,
		MaxConnectionReceiveWindow:       1 << 20,
	}
	connectionLimits := peerLimits{max: maxConnections, perIP: maxConnectionsPerIP, byIP: make(map[netip.Addr]int)}
	transport := &quic.Transport{
		Conn:                socket,
		VerifySourceAddress: func(net.Addr) bool { return true },
		ConnContext: func(ctx context.Context, client *quic.ClientInfo) (context.Context, error) {
			// Apply limits before the TLS handshake so an idle peer cannot
			// consume the remaining slots while avoiding session admission.
			ip := client.RemoteAddr.(*net.UDPAddr).AddrPort().Addr().WithZone("").Unmap()
			if !connectionLimits.tryAdd(ip) {
				return nil, errors.New("QUIC connection limit reached")
			}
			context.AfterFunc(ctx, func() { connectionLimits.remove(ip) })
			return ctx, nil
		},
	}
	defer transport.Close()
	listener, err := transport.Listen(&tls.Config{
		MinVersion: tls.VersionTLS13,
		NextProtos: []string{http3.NextProtoH3},
		GetCertificate: func(*tls.ClientHelloInfo) (*tls.Certificate, error) {
			return cert.Load(), nil
		},
	}, quicConfig)
	if err != nil {
		return err
	}
	defer listener.Close()

	h3 := &http3.Server{MaxHeaderBytes: 4096}
	webtransport.ConfigureHTTP3Server(h3)
	wt := &webtransport.Server{
		H3: h3,
		// Wallets connect from origins unrelated to the node. The endpoint
		// uses no browser credentials and all transactions require signatures.
		CheckOrigin: func(*http.Request) bool { return true },
	}
	defer wt.Close()
	limiter := peerLimits{max: maxSessions, perIP: maxSessionsPerIP, byIP: make(map[netip.Addr]int)}
	h3.Handler = http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		handleSession(wt, &limiter, backend, token, w, r)
	})

	ctx, cancel := context.WithCancel(ctx)
	done := make(chan struct{})
	var serveError error
	go func() {
		serveError = acceptConnections(ctx, listener, wt)
		close(done)
	}()
	defer func() {
		cancel()
		<-done
	}()

	encoder := json.NewEncoder(output)
	boundAddress := socket.LocalAddr().String()
	publish := func() error {
		hashes := [2 * sha256.Size]byte{}
		first := sha256.Sum256(current.Certificate[0])
		second := sha256.Sum256(next.Certificate[0])
		copy(hashes[:], first[:])
		copy(hashes[sha256.Size:], second[:])
		return encoder.Encode(advertisement{Address: boundAddress, Hashes: hex.EncodeToString(hashes[:])})
	}
	if err := publish(); err != nil {
		return err
	}
	timer := time.NewTimer(rotationInterval)
	defer timer.Stop()
	for {
		select {
		case <-ctx.Done():
			return nil
		case <-done:
			return serveError
		case <-timer.C:
			now := time.Now()
			if now.Before(next.Leaf.NotBefore) || !now.Before(next.Leaf.NotAfter) {
				return errors.New("next WebTransport certificate is outside its validity window")
			}
			following, err := makeCertificate(now.Add(rotationInterval - time.Minute))
			if err != nil {
				return err
			}
			current, next = next, following
			cert.Store(current)
			if err := publish(); err != nil {
				return err
			}
			timer.Reset(rotationInterval)
		}
	}
}

func acceptConnections(ctx context.Context, listener *quic.Listener, server *webtransport.Server) error {
	ctx, cancel := context.WithCancel(ctx)
	var workers sync.WaitGroup
	defer workers.Wait()
	defer cancel()
	for {
		conn, err := listener.Accept(ctx)
		if err != nil {
			if ctx.Err() != nil {
				return nil
			}
			return err
		}
		workers.Go(func() {
			lifetime, cancel := context.WithTimeout(ctx, connectionTimeout)
			defer cancel()
			stop := context.AfterFunc(lifetime, func() { _ = conn.CloseWithError(0, "") })
			defer stop()
			defer conn.CloseWithError(0, "")
			_ = server.ServeQUICConn(conn)
		})
	}
}

func certificatePair(now time.Time) (current, next *tls.Certificate, err error) {
	current, err = makeCertificate(now.Add(-time.Minute))
	if err != nil {
		return nil, nil, err
	}
	next, err = makeCertificate(now.Add(rotationInterval - time.Minute))
	return current, next, err
}

func makeCertificate(notBefore time.Time) (*tls.Certificate, error) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return nil, err
	}
	template := x509.Certificate{
		NotBefore:   notBefore,
		NotAfter:    notBefore.Add(certificateAge),
		KeyUsage:    x509.KeyUsageDigitalSignature,
		ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
	}
	der, err := x509.CreateCertificate(rand.Reader, &template, &template, &key.PublicKey, key)
	if err != nil {
		return nil, err
	}
	return &tls.Certificate{Certificate: [][]byte{der}, PrivateKey: key, Leaf: &template}, nil
}

type peerLimits struct {
	lock  sync.Mutex
	max   int
	perIP int
	size  int
	byIP  map[netip.Addr]int
}

func (l *peerLimits) tryAdd(ip netip.Addr) bool {
	l.lock.Lock()
	defer l.lock.Unlock()
	if l.size == l.max || l.byIP[ip] == l.perIP {
		return false
	}
	l.size++
	l.byIP[ip]++
	return true
}

func (l *peerLimits) remove(ip netip.Addr) {
	l.lock.Lock()
	defer l.lock.Unlock()
	l.size--
	if l.byIP[ip] == 1 {
		delete(l.byIP, ip)
	} else {
		l.byIP[ip]--
	}
}
