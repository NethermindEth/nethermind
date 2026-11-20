// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

package main

import (
	"context"
	"encoding/binary"
	"encoding/hex"
	"errors"
	"io"
	"net"
	"net/http"
	"net/netip"
	"strings"
	"time"

	"github.com/dunglas/httpsfv"
	"github.com/golang/snappy"
	"github.com/quic-go/webtransport-go"
)

const (
	noncePrefix  = "ENR-key-proof-v1="
	txProtocol   = "webtx/1"
	discProtocol = "webdisc/1"
	txMode       = byte(1)
	discMode     = byte(2)
	requestCode  = byte(0x10) // After the devp2p base protocol's 16 message IDs.
	responseCode = byte(0x11)
	maxRequest   = 256 << 10
	maxResponse  = 8 << 10
)

func handleSession(server *webtransport.Server, limiter *peerLimits, backend netip.AddrPort, token [32]byte, w http.ResponseWriter, r *http.Request) {
	if r.URL.Path != "/devp2p" {
		w.WriteHeader(http.StatusNotFound)
		return
	}
	if r.Method != http.MethodConnect {
		w.WriteHeader(http.StatusMethodNotAllowed)
		return
	}
	mode, nonce, ok := readOffer(r.Header.Values("WT-Available-Protocols"))
	if !ok {
		w.WriteHeader(http.StatusBadRequest)
		return
	}
	addr, err := netip.ParseAddrPort(r.RemoteAddr)
	if err != nil {
		w.WriteHeader(http.StatusBadRequest)
		return
	}
	ip := addr.Addr().WithZone("").Unmap()
	if !limiter.tryAdd(ip) {
		w.WriteHeader(http.StatusTooManyRequests)
		return
	}
	defer limiter.remove(ip)

	session, err := server.Upgrade(w, r)
	if err != nil {
		w.WriteHeader(http.StatusBadRequest)
		return
	}
	defer session.CloseWithError(0, "")
	ctx, cancel := context.WithTimeout(session.Context(), sessionTimeout)
	defer cancel()
	if err := exchange(ctx, session, backend, token, mode, nonce); err != nil {
		return
	}

	// A browser may still be consuming the response after the server's FIN.
	timer := time.NewTimer(2 * time.Second)
	defer timer.Stop()
	select {
	case <-session.Context().Done():
	case <-timer.C:
	}
}

func readOffer(headers []string) (mode byte, nonce [32]byte, ok bool) {
	list, err := httpsfv.UnmarshalList(headers)
	if err != nil {
		return 0, nonce, false
	}
	hasNonce := false
	for _, member := range list {
		item, ok := member.(httpsfv.Item)
		if !ok {
			continue
		}
		name, ok := item.Value.(string)
		if !ok {
			continue
		}
		switch name {
		case txProtocol:
			mode = txMode
		case discProtocol:
			if mode == 0 {
				mode = discMode
			}
		default:
			if encoded, ok := strings.CutPrefix(name, noncePrefix); ok {
				if hasNonce || len(encoded) != hex.EncodedLen(len(nonce)) {
					return 0, nonce, false
				}
				if _, err := hex.Decode(nonce[:], []byte(encoded)); err != nil {
					return 0, nonce, false
				}
				hasNonce = true
			}
		}
	}
	return mode, nonce, mode != 0 && hasNonce
}

func exchange(ctx context.Context, session *webtransport.Session, backend netip.AddrPort, token [32]byte, mode byte, nonce [32]byte) error {
	deadline, _ := ctx.Deadline()
	dialer := net.Dialer{}
	conn, err := dialer.DialContext(ctx, "tcp", backend.String())
	if err != nil {
		return err
	}
	defer conn.Close()
	stop := context.AfterFunc(ctx, func() { _ = conn.Close() })
	defer stop()
	if err := conn.SetDeadline(deadline); err != nil {
		return err
	}
	var prelude [65]byte
	copy(prelude[:32], token[:])
	prelude[32] = mode
	copy(prelude[33:], nonce[:])
	if _, err := conn.Write(prelude[:]); err != nil {
		return err
	}
	handshake, err := readRecord(conn, 128)
	if err != nil {
		return err
	}
	stream, err := session.OpenStreamSync(ctx)
	if err != nil {
		return err
	}
	if err := stream.SetDeadline(deadline); err != nil {
		return err
	}
	if _, err := stream.Write(handshake); err != nil {
		return err
	}

	maxPayload := maxRequest
	if mode == discMode {
		maxPayload = 1
	}
	request, err := readRequest(stream, maxPayload)
	if err != nil {
		return err
	}
	if mode == discMode && (len(request) != 1 || request[0] != 0xc0) {
		return errors.New("invalid peer discovery request")
	}
	if err := writeRecord(conn, request); err != nil {
		return err
	}
	reply, err := readRecord(conn, maxResponse)
	if err != nil {
		return err
	}
	if err := writeResponse(stream, reply); err != nil {
		return err
	}
	return stream.Close()
}

func readRequest(r io.Reader, limit int) ([]byte, error) {
	var frame [4]byte
	if _, err := io.ReadFull(r, frame[:]); err != nil {
		return nil, err
	}
	size := int(frame[1])<<16 | int(frame[2])<<8 | int(frame[3])
	if frame[0] != requestCode || size == 0 || size > snappy.MaxEncodedLen(limit) {
		return nil, errors.New("invalid request frame")
	}
	payload := make([]byte, size)
	if _, err := io.ReadFull(r, payload); err != nil {
		return nil, err
	}
	decoded, err := snappy.DecodedLen(payload)
	if err != nil || decoded == 0 || decoded > limit {
		return nil, errors.New("invalid decoded request length")
	}
	return snappy.Decode(nil, payload)
}

func writeResponse(w io.Writer, response []byte) error {
	payload := snappy.Encode(nil, response)
	size := len(payload)
	frame := [4]byte{responseCode, byte(size >> 16), byte(size >> 8), byte(size)}
	if _, err := w.Write(frame[:]); err != nil {
		return err
	}
	_, err := w.Write(payload)
	return err
}

func readRecord(r io.Reader, limit uint32) ([]byte, error) {
	var header [4]byte
	if _, err := io.ReadFull(r, header[:]); err != nil {
		return nil, err
	}
	size := binary.BigEndian.Uint32(header[:])
	if size == 0 || size > limit {
		return nil, errors.New("invalid backend record length")
	}
	record := make([]byte, size)
	_, err := io.ReadFull(r, record)
	return record, err
}

func writeRecord(w io.Writer, payload []byte) error {
	var header [4]byte
	binary.BigEndian.PutUint32(header[:], uint32(len(payload)))
	if _, err := w.Write(header[:]); err != nil {
		return err
	}
	_, err := w.Write(payload)
	return err
}
