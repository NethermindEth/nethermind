import ctypes
import errno
import os
from pathlib import Path
import runpy
import signal
import sys
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).with_name('spin-diagnostic')))
import pidfd_compat as pc


class BindingsTests(unittest.TestCase):
    def setUp(self):
        self.platform = patch.object(sys,'platform','linux')
        self.platform.start(); self.addCleanup(self.platform.stop)

    def libc(self):
        library=Mock()
        library.gnu_get_libc_version.return_value=b'2.39'
        library.pidfd_open.return_value=71
        library.pidfd_send_signal.return_value=0
        return library

    def test_missing_native_uses_typed_glibc_exact_fd(self):
        library=self.libc()
        with patch.object(os,'pidfd_open',None,create=True), patch.object(signal,'pidfd_send_signal',None,create=True), patch.object(ctypes,'CDLL',return_value=library), patch.object(os,'kill') as numeric:
            fd=pc.pidfd_open(123)
            pc.pidfd_send_signal(fd,signal.SIGINT)
        library.pidfd_open.assert_called_once_with(123,0)
        library.pidfd_send_signal.assert_called_once_with(71,signal.SIGINT,None,0)
        self.assertEqual(library.pidfd_send_signal.argtypes,[ctypes.c_int,ctypes.c_int,ctypes.c_void_p,ctypes.c_uint])
        self.assertEqual(library.pidfd_open.argtypes,[ctypes.c_int,ctypes.c_uint])
        numeric.assert_not_called()

    def test_native_used_without_loading_libc(self):
        with patch.object(os,'pidfd_open',Mock(return_value=19),create=True) as opening, patch.object(signal,'pidfd_send_signal',Mock(),create=True) as sending, patch.object(ctypes,'CDLL') as loader:
            self.assertEqual(pc.pidfd_open(123),19); pc.pidfd_send_signal(19,0)
        opening.assert_called_once_with(123); sending.assert_called_once_with(19,0); loader.assert_not_called()

    def test_native_kernel_error_is_not_retried_via_libc(self):
        with patch.object(os,'pidfd_open',Mock(side_effect=OSError(errno.EPERM,'blocked')),create=True), patch.object(ctypes,'CDLL') as loader:
            with self.assertRaises(PermissionError): pc.pidfd_open(123)
        loader.assert_not_called()

    def test_non_linux_fails_before_any_binding(self):
        with patch.object(sys,'platform','win32'), patch.object(ctypes,'CDLL') as loader:
            with self.assertRaises(OSError): pc.pidfd_open(123)
            with self.assertRaises(OSError): pc.pidfd_send_signal(71,0)
        loader.assert_not_called()

    def test_missing_libc_or_symbols_fails_closed(self):
        for library in [Mock(spec=[]),Mock(spec=['gnu_get_libc_version'])]:
            if hasattr(library,'gnu_get_libc_version'): library.gnu_get_libc_version.return_value=b'2.39'
            with self.subTest(library=library), patch.object(os,'pidfd_open',None,create=True), patch.object(ctypes,'CDLL',return_value=library):
                with self.assertRaises(OSError) as error: pc.pidfd_open(123)
                self.assertEqual(error.exception.errno,errno.ENOSYS)
        with patch.object(os,'pidfd_open',None,create=True), patch.object(ctypes,'CDLL',side_effect=OSError('no libc')):
            with self.assertRaises(OSError): pc.pidfd_open(123)
        library=Mock(spec=['gnu_get_libc_version'])
        library.gnu_get_libc_version.return_value=b'2.39'
        with patch.object(signal,'pidfd_send_signal',None,create=True), patch.object(ctypes,'CDLL',return_value=library):
            with self.assertRaises(OSError) as error: pc.pidfd_send_signal(71,0)
            self.assertEqual(error.exception.errno,errno.ENOSYS)

    def test_errno_preserved_for_open_and_send(self):
        for code in [errno.ENOSYS,errno.EPERM,errno.ESRCH,errno.EBADF]:
            library=self.libc()
            def failure(*args): ctypes.set_errno(code); return -1
            library.pidfd_open.side_effect=failure; library.pidfd_send_signal.side_effect=failure
            with self.subTest(errno=code), patch.object(os,'pidfd_open',None,create=True), patch.object(signal,'pidfd_send_signal',None,create=True), patch.object(ctypes,'CDLL',return_value=library):
                for action in [lambda:pc.pidfd_open(123),lambda:pc.pidfd_send_signal(71,0)]:
                    with self.assertRaises(OSError) as error: action()
                    self.assertEqual(error.exception.errno,code)

    def test_preflight_closes_fd_after_success_or_send_failure(self):
        for fail in [False,True]:
            with self.subTest(fail=fail), patch.object(pc,'pidfd_open',return_value=71), patch.object(pc,'pidfd_send_signal',side_effect=OSError(errno.EPERM,'blocked') if fail else None), patch.object(os,'close') as close:
                if fail:
                    with self.assertRaises(OSError): pc.preflight()
                else: pc.preflight()
                close.assert_called_once_with(71)

    def test_preflight_open_failure_does_not_close_unknown_fd(self):
        with patch.object(pc,'pidfd_open',side_effect=OSError(errno.ENOSYS,'unavailable')), patch.object(os,'close') as close:
            with self.assertRaises(OSError): pc.preflight()
        close.assert_not_called()

    def test_isolated_launcher_missing_host_glibc_never_executes(self):
        with patch.dict(sys.modules,{'resource':Mock(RLIMIT_FSIZE=1)}), patch.object(sys,'argv',['limited_exec.py','7','1048576','/usr/bin/perf']), patch.object(ctypes,'CDLL',return_value=Mock(spec=[])), patch.object(os,'umask'), patch.object(os,'execv') as execute:
            with self.assertRaises(OSError):
                runpy.run_path(str(Path(pc.__file__).with_name('limited_exec.py')),run_name='__main__')
        execute.assert_not_called()


if __name__=='__main__': unittest.main(verbosity=2)
