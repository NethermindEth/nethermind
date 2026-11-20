// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

package main

import (
	"bytes"
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"net"
	"net/netip"
	"strings"
	"testing"
	"time"

	"github.com/golang/snappy"
	"github.com/quic-go/webtransport-go"
)

func TestOffer(t *testing.T) {
	nonce := strings.Repeat("ab", 32)
	challenge := "\"" + noncePrefix + nonce + "\""
	for _, tc := range []struct {
		name   string
		header []string
		mode   byte
	}{
		{"transaction", []string{challenge, "\"webtx/1\""}, txMode},
		{"discovery", []string{challenge + ", \"webdisc/1\""}, discMode},
		{"prefer transaction", []string{challenge + ", \"webdisc/1\", \"webtx/1\""}, txMode},
		{"missing nonce", []string{"\"webtx/1\""}, 0},
		{"duplicate nonce", []string{"\"webtx/1\", " + challenge, challenge}, 0},
		{"short nonce", []string{"\"webtx/1\", \"" + noncePrefix + "ab\""}, 0},
		{"invalid nonce", []string{"\"webtx/1\", \"" + noncePrefix + strings.Repeat("xz", 32) + "\""}, 0},
		{"unknown capability", []string{challenge + ", \"eth/72\""}, 0},
		{"malformed list", []string{challenge + ", webtx/1?"}, 0},
	} {
		t.Run(tc.name, func(t *testing.T) {
			mode, actualNonce, ok := readOffer(tc.header)
			if ok != (tc.mode != 0) || ok && (mode != tc.mode || hex.EncodeToString(actualNonce[:]) != nonce) {
				t.Fatalf("unexpected offer result (%d, %x, %v)", mode, actualNonce, ok)
			}
		})
	}
}

func TestReadRequest(t *testing.T) {
	for _, size := range []int{maxRequest - 1, maxRequest, maxRequest + 1} {
		payload := bytes.Repeat([]byte{0xc0}, size)
		read, err := readRequest(bytes.NewReader(requestFrame(payload)), maxRequest)
		if size > maxRequest {
			if err == nil {
				t.Fatal("accepted a request larger than the decoded limit")
			}
		} else if err != nil || !bytes.Equal(read, payload) {
			t.Fatalf("request of size %d did not round-trip: %v", size, err)
		}
	}
	for _, frame := range [][]byte{
		{responseCode, 0, 0, 1, 0},
		{requestCode, 0xff, 0xff, 0xff},
		{requestCode, 0, 0, 4, 1, 0, 0xc0, 0}, // bytes after the complete Snappy block
		{requestCode, 0, 0, 4, 1, 0, 0xc0},    // truncated frame
	} {
		if _, err := readRequest(bytes.NewReader(frame), maxRequest); err == nil {
			t.Fatalf("accepted invalid request %x", frame)
		}
	}
}

func TestPeerLimits(t *testing.T) {
	limits := peerLimits{max: 3, perIP: 2, byIP: make(map[netip.Addr]int)}
	first := netip.MustParseAddr("192.0.2.1")
	second := netip.MustParseAddr("192.0.2.2")
	if !limits.tryAdd(first) || !limits.tryAdd(first) || limits.tryAdd(first) {
		t.Fatal("per-IP limit was not enforced")
	}
	if !limits.tryAdd(second) || limits.tryAdd(second) {
		t.Fatal("shared limit was not enforced")
	}
	limits.remove(first)
	if !limits.tryAdd(second) {
		t.Fatal("slot did not become available")
	}
	limits.remove(first)
	limits.remove(second)
	limits.remove(second)
	if limits.size != 0 || len(limits.byIP) != 0 {
		t.Fatal("limiter retained released slots")
	}
}

func TestWebTransport(t *testing.T) {
	for _, tc := range []struct {
		name    string
		mode    byte
		request []byte
		reply   []byte
	}{
		{txProtocol, txMode, []byte{0xc1, 0xc0}, []byte{0xc2, 0xc1, 0x80}},
		{discProtocol, discMode, []byte{0xc0}, []byte{0xc0}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			ctx, cancel := context.WithTimeout(t.Context(), 10*time.Second)
			defer cancel()
			deadline, _ := ctx.Deadline()
			backend, err := net.ListenTCP("tcp4", &net.TCPAddr{IP: net.IPv4(127, 0, 0, 1)})
			if err != nil {
				t.Fatal(err)
			}
			defer backend.Close()
			if err := backend.SetDeadline(deadline); err != nil {
				t.Fatal(err)
			}
			token, nonce := [32]byte{0x11, 0xa3}, [32]byte{0x22, 0x07}
			handshake := []byte{0xc2, 0x81, 0xa3}
			backendDone := make(chan error, 1)
			go func() {
				conn, err := backend.AcceptTCP()
				if err != nil {
					backendDone <- err
					return
				}
				defer conn.Close()
				if err := conn.SetDeadline(deadline); err != nil {
					backendDone <- err
					return
				}
				backendDone <- fakeBackend(conn, token, tc.mode, nonce, handshake, tc.request, tc.reply)
			}()

			report, output := io.Pipe()
			defer report.Close()
			listenDone := make(chan error, 1)
			go func() {
				err := listen(ctx, netip.MustParseAddrPort("127.0.0.1:0"), backend.Addr().(*net.TCPAddr).AddrPort(), token, output)
				_ = output.CloseWithError(err)
				listenDone <- err
			}()
			var ready advertisement
			if err := json.NewDecoder(report).Decode(&ready); err != nil {
				t.Fatal(err)
			}
			hashes, err := hex.DecodeString(ready.Hashes)
			if err != nil || len(hashes) != 2*sha256.Size || bytes.Equal(hashes[:sha256.Size], hashes[sha256.Size:]) {
				t.Fatalf("invalid certificate hashes: %q", ready.Hashes)
			}
			dialer := &webtransport.Dialer{
				ApplicationProtocols: []string{noncePrefix + hex.EncodeToString(nonce[:]), tc.name},
				TLSClientConfig: &tls.Config{
					InsecureSkipVerify: true, // The browser validates the advertised DER hash instead of the CA.
					VerifyConnection: func(state tls.ConnectionState) error {
						cert := state.PeerCertificates[0]
						actual := sha256.Sum256(cert.Raw)
						if !bytes.Equal(actual[:], hashes[:sha256.Size]) {
							return errors.New("TLS certificate does not match advertised hash")
						}
						if key, ok := cert.PublicKey.(*ecdsa.PublicKey); !ok || key.Curve != elliptic.P256() {
							return errors.New("TLS certificate must use P-256")
						}
						if cert.NotAfter.Sub(cert.NotBefore) != certificateAge || cert.KeyUsage&x509.KeyUsageDigitalSignature == 0 {
							return errors.New("TLS certificate does not meet the WebTransport pinning requirements")
						}
						return nil
					},
				},
			}
			defer dialer.Close()
			_, session, err := dialer.Dial(ctx, "https://"+ready.Address+"/devp2p", nil)
			if err != nil {
				t.Fatal(err)
			}
			defer session.CloseWithError(0, "")
			stream, err := session.AcceptStream(ctx)
			if err != nil {
				t.Fatal(err)
			}
			if err := stream.SetDeadline(deadline); err != nil {
				t.Fatal(err)
			}
			gotHandshake := make([]byte, len(handshake))
			if _, err := io.ReadFull(stream, gotHandshake); err != nil || !bytes.Equal(gotHandshake, handshake) {
				t.Fatalf("wrong handshake %x: %v", gotHandshake, err)
			}
			if _, err := stream.Write(requestFrame(tc.request)); err != nil {
				t.Fatal(err)
			}
			reply, err := io.ReadAll(stream)
			if err != nil {
				t.Fatal(err)
			}
			if len(reply) < 4 || reply[0] != responseCode || len(reply)-4 != int(reply[1])<<16|int(reply[2])<<8|int(reply[3]) {
				t.Fatalf("invalid response frame %x", reply)
			}
			decoded, err := snappy.Decode(nil, reply[4:])
			if err != nil || !bytes.Equal(decoded, tc.reply) {
				t.Fatalf("wrong response %x: %v", decoded, err)
			}
			if err := <-backendDone; err != nil {
				t.Fatal(err)
			}
			cancel()
			if err := <-listenDone; err != nil {
				t.Fatal(err)
			}
		})
	}
}

func requestFrame(payload []byte) []byte {
	block := snappy.Encode(nil, payload)
	size := len(block)
	return append([]byte{requestCode, byte(size >> 16), byte(size >> 8), byte(size)}, block...)
}

func fakeBackend(conn net.Conn, token [32]byte, mode byte, nonce [32]byte, handshake, request, reply []byte) error {
	var prelude [65]byte
	if _, err := io.ReadFull(conn, prelude[:]); err != nil {
		return err
	}
	if !bytes.Equal(token[:], prelude[:32]) || prelude[32] != mode || !bytes.Equal(nonce[:], prelude[33:]) {
		return errors.New("incorrect bridge prelude")
	}
	if err := writeRecord(conn, handshake); err != nil {
		return err
	}
	got, err := readRecord(conn, maxRequest)
	if err != nil {
		return err
	}
	if !bytes.Equal(got, request) {
		return errors.New("incorrect bridge request")
	}
	return writeRecord(conn, reply)
}
