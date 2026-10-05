"""Linux pidfd bindings; libc fallback never signals a numeric PID."""
import ctypes
import errno
import os
import signal
import sys


def _linux():
    if sys.platform != 'linux':
        raise OSError(errno.ENOSYS, 'Linux pidfd support required')


def host_libc():
    _linux()
    library = ctypes.CDLL(None, use_errno=True)
    try:
        version = library.gnu_get_libc_version
    except AttributeError as error:
        raise OSError(errno.ENOSYS, 'host glibc required') from error
    version.argtypes, version.restype = [], ctypes.c_char_p
    if not version():
        raise OSError(errno.ENOSYS, 'host glibc version unavailable')
    return library


def _function(name, arguments):
    try:
        function = getattr(host_libc(), name)
    except AttributeError as error:
        raise OSError(errno.ENOSYS, 'host glibc '+name+' unavailable') from error
    function.argtypes, function.restype = arguments, ctypes.c_int
    return function


def pidfd_open(pid):
    _linux()
    native = getattr(os, 'pidfd_open', None)
    if callable(native):
        return native(pid)
    function = _function('pidfd_open', [ctypes.c_int, ctypes.c_uint])
    ctypes.set_errno(0)
    descriptor = function(pid, 0)
    if descriptor < 0:
        code = ctypes.get_errno() or errno.EIO
        raise OSError(code, os.strerror(code))
    return descriptor


def pidfd_send_signal(descriptor, number):
    _linux()
    native = getattr(signal, 'pidfd_send_signal', None)
    if callable(native):
        return native(descriptor, number)
    function = _function('pidfd_send_signal', [ctypes.c_int, ctypes.c_int, ctypes.c_void_p, ctypes.c_uint])
    ctypes.set_errno(0)
    if function(descriptor, number, None, 0) < 0:
        code = ctypes.get_errno() or errno.EIO
        raise OSError(code, os.strerror(code))


def preflight():
    descriptor = pidfd_open(os.getpid())
    try:
        pidfd_send_signal(descriptor, 0)
    finally:
        os.close(descriptor)


def self_check():
    """No privileges/profilers: exercise own child's pidfd signal and parent check."""
    _linux()
    import select
    import subprocess
    from pathlib import Path
    library = host_libc()
    version = library.gnu_get_libc_version().decode('ascii')
    preflight()
    descriptor = None
    child = subprocess.Popen(
        [sys.executable, '-I', str(Path(__file__).with_name('limited_exec.py')),
         str(os.getpid()), '1048576', sys.executable, '-I', '-c',
         "import signal,sys,select; signal.signal(signal.SIGINT,signal.SIG_DFL); "
         "print('ready',flush=True); select.select([sys.stdin],[],[],5)"],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    try:
        ready, _, _ = select.select([child.stdout], [], [], 3)
        if not ready or child.stdout.readline() != b'ready\n':
            raise RuntimeError('bounded launcher readiness failed')
        descriptor = pidfd_open(child.pid)
        if os.get_inheritable(descriptor):
            raise RuntimeError('pidfd must be close-on-exec')
        pidfd_send_signal(descriptor, 0)
        pidfd_send_signal(descriptor, signal.SIGINT)
        code = child.wait(timeout=3)
        if code != -signal.SIGINT:
            raise RuntimeError('owned pidfd SIGINT did not terminate child')
        return dict(status='PASS_LINUX_PIDFD_CAPABILITY_ONLY', python=sys.version,
                    interpreter=sys.executable, glibc=version,
                    signal_module=getattr(signal,'__file__',None),
                    os_module=getattr(os,'__file__',None),
                    stdlib_open=callable(getattr(os,'pidfd_open',None)),
                    stdlib_send=callable(getattr(signal,'pidfd_send_signal',None)),
                    returncode=code)
    finally:
        if descriptor is not None:
            os.close(descriptor)
        # Finite child exits itself after5s; EOF also ends select. Never kill by PID.
        child.stdin.close()
        child.wait(timeout=8)
        child.stdout.close()
        child.stderr.close()


if __name__ == '__main__':
    import argparse
    import json
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--self-check', action='store_true', required=True)
    parser.parse_args()
    try:
        result = self_check()
    except Exception as error:
        result = dict(status='FAIL', error_type=type(error).__name__, errno=getattr(error,'errno',None), error=str(error))
    result.update(python=sys.version, interpreter=sys.executable,
                  signal_module=getattr(signal,'__file__',None),
                  os_module=getattr(os,'__file__',None),
                  stdlib_open=callable(getattr(os,'pidfd_open',None)),
                  stdlib_send=callable(getattr(signal,'pidfd_send_signal',None)))
    print(json.dumps(result,indent=2))
    raise SystemExit(0 if result['status']=='PASS_LINUX_PIDFD_CAPABILITY_ONLY' else 2)
