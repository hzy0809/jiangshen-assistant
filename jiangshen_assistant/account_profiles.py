"""Strict account profiles in a private, atomic credential store.

Passwords are stored as entered. Call ``public_view`` before exposing a document
to diagnostics or a control API. A malformed existing file always blocks writes;
there is no automatic repair, migration, or plaintext backup.
"""

from __future__ import annotations

from contextlib import contextmanager
import json
import os
from pathlib import Path
import stat
import threading
import uuid


TASK_CATALOG: dict[str, str] = {
    'daily': '日常',
    'campaign': '征战',
    'elite': '精英挑战',
    'challenge': '挑战',
    'soul': '聚魂',
    'tribute': '灵兽上贡',
    'equipment': '装备培养',
    'star': '星灵',
    'growth': '成长任务',
    'claim_growth': '领取成长奖励',
}

DAILY_CATALOG: dict[str, str] = {
    'sign': '签到及领奖',
    'online_gift': '礼包领取',
    'vip': 'VIP 奖励',
    'events': '精彩活动',
    'online_reward': '在线奖励',
    'limited': '限时活动',
    'copy': '抄录',
    'tribute': '灵兽上贡',
    'stone': '采石',
    'divinity': '击败天神',
    'mingshi': '收获铭石',
    'soul': '免费聚魂',
    'refine': '炼魂',
    'wood': '木人试炼',
    'guild_lottery': '同盟抽奖',
    'guild_donation': '同盟捐献',
    'grave': '地宫探宝',
    'arena': '排位竞技场挑战',
    'point_race': '分组积分赛挑战',
    'flower': '送花',
    'friend_monster': '协助好友降妖',
    'trial': '名将试炼',
    'home_monster': '主城降妖',
    'revenge': '复仇魔王战斗与收取',
    'quiz': '参加答题',
    'brawl': '参加比武大会',
    'training': '消灭训练妖魔',
    'category_rewards': '日常分类奖励',
    'spirit_tree': '灵气树收取',
    'city_shop': '商铺收取与生产',
    'little_demon': '小妖收取',
}

CITY_DAILY_KEYS = frozenset(('spirit_tree', 'city_shop', 'little_demon', 'online_gift'))
SHOP_DURATIONS = {0: '只收取', 1200: '钱币 · 20 分钟', 3600: '钱币 · 1 小时', 43200: '钱币 · 12 小时'}
DAILY_CATEGORY_LABELS = {
    2: '变强捷径', 3: '成名秘籍', 4: '升级宝典',
    5: '同盟活动', 6: '定时任务', 7: '奖励',
}

# Presentation order is shared by the account editor and the standalone page.
# Keys and persisted selections remain independent of their visual grouping.
DAILY_GROUPS: tuple[tuple[str, tuple[str, ...]], ...] = (
    ('签到与奖励', ('sign', 'online_gift', 'vip', 'events', 'online_reward', 'limited', 'category_rewards')),
    ('培养与资源', ('copy', 'tribute', 'stone', 'soul', 'refine')),
    ('天神与铭石', ('divinity', 'mingshi')),
    ('挑战与竞技', ('wood', 'arena', 'point_race', 'grave', 'trial', 'training')),
    ('同盟与好友', ('guild_lottery', 'guild_donation', 'flower', 'friend_monster')),
    ('其他活动', ('home_monster', 'revenge', 'quiz', 'brawl')),
    ('主城', ('spirit_tree', 'city_shop', 'little_demon')),
)

_TASK_KEYS = tuple(TASK_CATALOG)
_DAILY_KEYS = tuple(DAILY_CATALOG)
RETIRED_DAILY_KEYS = frozenset(('salary', 'arena_chest'))
_PROFILE_KEYS = (
    'id', 'label', 'login_name', 'password', 'server_name', 'server_tab', 'role_name',
    'user_id', 'zone_id', 'role_id', 'tasks',
)


class AccountProfileError(ValueError):
    """A deliberately credential-free message suitable for display."""


class AccountProfilePermissionError(AccountProfileError):
    """The private storage boundary could not be established."""


def _object(value: object, keys: tuple[str, ...], message: str) -> dict:
    if (type(value) is not dict or any(type(key) is not str for key in value)
            or set(value) != set(keys)):
        raise AccountProfileError(message)
    return value


def _string(value: object, message: str) -> str:
    if type(value) is not str:
        raise AccountProfileError(message)
    return value


def _boolean(value: object, message: str) -> bool:
    if type(value) is not bool:
        raise AccountProfileError(message)
    return value


def _integer(value: object, minimum: int, maximum: int | None, message: str) -> int:
    if type(value) is not int or value < minimum or (maximum is not None and value > maximum):
        raise AccountProfileError(message)
    return value


def _identifier(value: object) -> str:
    value = _string(value, '账号配置 ID 必须是 UUID 文本。')
    try:
        canonical = str(uuid.UUID(value))
    except (ValueError, AttributeError):
        raise AccountProfileError('账号配置 ID 必须是 UUID 文本。') from None
    if canonical != value:
        raise AccountProfileError('账号配置 ID 必须使用标准小写 UUID 格式。')
    return value


def _options(task: str, value: object) -> dict:
    if task == 'daily':
        if type(value) is dict and set(value) == {'donation_type', 'items'}:
            value = {**value, 'shop_duration': 3600}
        options = _object(value, ('donation_type', 'items', 'shop_duration'), '日常任务选项字段不完整或含未知字段。')
        donation = _integer(options['donation_type'], 1, 3, '捐献类型必须是 1、2 或 3。')
        duration = options['shop_duration']
        if type(duration) is not int or duration not in SHOP_DURATIONS:
            raise AccountProfileError('商铺生产时长无效。')
        items = options['items']
        if type(items) is dict:
            keys = set(items)
            required = set(_DAILY_KEYS) - CITY_DAILY_KEYS - {'online_reward'}
            retired = keys & RETIRED_DAILY_KEYS
            if (required <= keys <= (set(_DAILY_KEYS) | RETIRED_DAILY_KEYS)
                    and retired in (set(), RETIRED_DAILY_KEYS)):
                for item in items.values():
                    _boolean(item, '日常子任务开关必须是布尔值。')
                # The requested one-time gift is enabled unless explicitly
                # disabled; the other new city routes preserve existing plans.
                items = {key: items.get(key, key == 'online_gift') for key in _DAILY_KEYS}
        items = _object(items, _DAILY_KEYS, '日常子任务字段不完整或含未知字段。')
        return {'donation_type': donation, 'shop_duration': duration, 'items': {
            key: _boolean(items[key], '日常子任务开关必须是布尔值。') for key in _DAILY_KEYS
        }}
    if task == 'campaign':
        options = _object(value, ('allow_growth',), '征战选项字段不完整或含未知字段。')
        return {'allow_growth': _boolean(options['allow_growth'], '征战成长开关必须是布尔值。')}
    if task == 'elite':
        options = _object(value, ('target', 'campaign_id', 'star'), '精英挑战选项字段不完整或含未知字段。')
        return {
            'target': _integer(options['target'], 1, 10, '精英挑战目标次数必须是 1 到 10 的整数。'),
            'campaign_id': _integer(options['campaign_id'], 0, 65535,
                                    '精英关卡编号必须是 0 到 65535 的整数。'),
            'star': _integer(options['star'], 0, 3, '精英星级必须是 0、1、2 或 3。'),
        }
    if task == 'soul':
        options = _object(value, ('blue_count', 'purple_count'), '聚魂选项字段不完整或含未知字段。')
        return {key: _integer(options[key], 0, 100, '聚魂次数必须是 0 到 100 的整数。')
                for key in ('blue_count', 'purple_count')}
    if task == 'tribute':
        options = _object(value, ('max_requests', 'use_all', 'target_level'),
                          '上贡选项字段不完整或含未知字段。')
        return {
            'max_requests': _integer(options['max_requests'], 1, 100, '上贡次数上限必须是 1 到 100 的整数。'),
            'use_all': _boolean(options['use_all'], '上贡全部使用开关必须是布尔值。'),
            'target_level': _integer(options['target_level'], 0, 100, '上贡目标等级必须是 0 到 100 的整数。'),
        }
    if task == 'equipment':
        options = _object(value, ('equipment_id', 'desired_level', 'mode', 'max_requests', 'max_money', 'max_stone'),
                          '装备选项字段不完整或含未知字段。')
        mode = _string(options['mode'], '装备培养方式必须是文本。')
        if mode not in ('enchant', 'evolve'):
            raise AccountProfileError('装备培养方式必须是 enchant 或 evolve。')
        return {
            'equipment_id': _integer(options['equipment_id'], 0, 255, '装备编号必须是 0 到 255 的整数。'),
            'desired_level': _integer(options['desired_level'], 0, 255, '装备目标等级必须是 0 到 255 的整数。'),
            'mode': mode,
            'max_requests': _integer(options['max_requests'], 1, 100, '装备次数上限必须是 1 到 100 的整数。'),
            'max_money': _integer(options['max_money'], 0, None, '装备铜钱上限必须是非负整数。'),
            'max_stone': _integer(options['max_stone'], 0, None, '装备强化石上限必须是非负整数。'),
        }
    if task == 'star':
        options = _object(value, ('max_practice',), '星灵选项字段不完整或含未知字段。')
        practice = options['max_practice']
        if practice is not None:
            practice = _integer(practice, 0, None, '星灵修炼上限必须是空值或非负整数。')
        return {'max_practice': practice}
    _object(value, (), '当前任务不接受额外选项。')
    return {}


def _profile(value: object) -> dict:
    # Profiles saved before the server-category selector remain valid.
    if type(value) is dict and set(value) == set(_PROFILE_KEYS) - {'server_tab'}:
        value = {**value, 'server_tab': ''}
    profile = _object(value, _PROFILE_KEYS, '账号配置字段不完整或含未知字段。')
    result = {'id': _identifier(profile['id'])}
    for key in ('label', 'login_name', 'password', 'server_name', 'server_tab', 'role_name', 'user_id', 'zone_id', 'role_id'):
        result[key] = _string(profile[key], '账号配置文本字段类型无效。')
    if result['server_tab'] not in ('', 'tab_nav_web', 'tab_nav_sd', 'tab_nav_mobile'):
        raise AccountProfileError('区服分类无效。')
    for key, label in (('user_id', '账号 ID'), ('zone_id', '区服 ID'), ('role_id', '角色 ID')):
        identity = result[key]
        if identity and (not identity.isascii() or not identity.isdecimal() or int(identity) == 0):
            raise AccountProfileError(label + ' 必须是非零十进制文本或空文本。')
    tasks = _object(profile['tasks'], _TASK_KEYS, '任务配置字段不完整或含未知任务。')
    result['tasks'] = {}
    for key in _TASK_KEYS:
        task = _object(tasks[key], ('enabled', 'options'), '任务字段必须包含 enabled 和 options。')
        result['tasks'][key] = {
            'enabled': _boolean(task['enabled'], '任务开关必须是布尔值。'),
            'options': _options(key, task['options']),
        }
    return result


def validate_document(value: object) -> dict:
    """Return an independent validated copy; never include input in errors."""
    document = _object(value, ('schema_version', 'selected_ids', 'profiles'),
                       '账号配置文档字段不完整或含未知字段。')
    if type(document['schema_version']) is not int or document['schema_version'] != 1:
        raise AccountProfileError('不支持的账号配置版本。')
    if type(document['profiles']) is not list or type(document['selected_ids']) is not list:
        raise AccountProfileError('账号列表和选择列表必须是数组。')
    profiles = [_profile(profile) for profile in document['profiles']]
    ids = {profile['id'] for profile in profiles}
    if len(ids) != len(profiles):
        raise AccountProfileError('账号配置 ID 不得重复。')
    selected = [_identifier(profile_id) for profile_id in document['selected_ids']]
    if len(set(selected)) != len(selected) or any(profile_id not in ids for profile_id in selected):
        raise AccountProfileError('所选账号必须存在且不得重复。')
    return {'schema_version': 1, 'selected_ids': selected, 'profiles': profiles}


def new_profile() -> dict:
    """Create an incomplete account with tasks disabled and the gift selected."""
    defaults = {
        'daily': {'donation_type': 2, 'shop_duration': 3600,
                  'items': {key: key == 'online_gift' for key in _DAILY_KEYS}},
        'campaign': {'allow_growth': False},
        'elite': {'target': 10, 'campaign_id': 0, 'star': 0},
        'challenge': {},
        'soul': {'blue_count': 0, 'purple_count': 0},
        'tribute': {'max_requests': 1, 'use_all': False, 'target_level': 0},
        'equipment': {'equipment_id': 0, 'desired_level': 0, 'mode': 'enchant',
                      'max_requests': 1, 'max_money': 0, 'max_stone': 0},
        'star': {'max_practice': None},
        'growth': {},
        'claim_growth': {},
    }
    return {
        'id': str(uuid.uuid4()), 'label': '新账号', 'login_name': '', 'password': '',
        'server_name': '', 'server_tab': '', 'role_name': '', 'user_id': '', 'zone_id': '', 'role_id': '',
        'tasks': {key: {'enabled': False, 'options': defaults[key]} for key in _TASK_KEYS},
    }


def public_view(document: object) -> dict:
    """Return an independent view without any password field or value."""
    result = validate_document(document)
    for profile in result['profiles']:
        profile['has_password'] = bool(profile.pop('password'))
    return result


def _unique_pairs(pairs: list[tuple[str, object]]) -> dict:
    result = {}
    for key, value in pairs:
        if key in result:
            raise AccountProfileError('账号配置文件含重复字段。')
        result[key] = value
    return result


def _reject_constant(value: str) -> None:
    raise AccountProfileError('账号配置文件含无效数值。')


def _decode(raw: bytes) -> dict:
    try:
        document = json.loads(raw.decode('utf-8-sig'), object_pairs_hook=_unique_pairs,
                              parse_constant=_reject_constant)
    except (ValueError, UnicodeError, RecursionError):
        # JSON exceptions can retain the original document. Do not expose their
        # strings, causes, snippets, line contents, or parser diagnostics.
        raise AccountProfileError('账号配置文件无效；原文件保持不变，请先处理原文件。') from None
    return validate_document(document)


class _WindowsSecurity:
    """Protected DACL and handles without any credential-bearing subprocess."""

    def __init__(self, directory: Path) -> None:
        import ctypes
        from ctypes import wintypes

        self.ctypes = ctypes
        self.wintypes = wintypes
        self.directory = directory
        self.directory_handle = None
        self.descriptor = ctypes.c_void_p()
        self.owner = ctypes.c_void_p()
        self.dacl = ctypes.c_void_p()
        self.kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        self.advapi = ctypes.WinDLL('advapi32', use_last_error=True)
        pointer = ctypes.c_void_p
        self.kernel.GetCurrentProcess.argtypes = []
        self.kernel.GetCurrentProcess.restype = wintypes.HANDLE
        self.kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        self.kernel.CloseHandle.restype = wintypes.BOOL
        self.kernel.LocalFree.argtypes = [pointer]
        self.kernel.LocalFree.restype = pointer
        self.advapi.OpenProcessToken.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE)]
        self.advapi.OpenProcessToken.restype = wintypes.BOOL
        self.advapi.GetTokenInformation.argtypes = [wintypes.HANDLE, ctypes.c_int, pointer,
                                                  wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
        self.advapi.GetTokenInformation.restype = wintypes.BOOL
        self.advapi.ConvertSidToStringSidW.argtypes = [pointer, ctypes.POINTER(wintypes.LPWSTR)]
        self.advapi.ConvertSidToStringSidW.restype = wintypes.BOOL
        self.advapi.ConvertStringSecurityDescriptorToSecurityDescriptorW.argtypes = [
            wintypes.LPCWSTR, wintypes.DWORD, ctypes.POINTER(pointer), ctypes.POINTER(wintypes.DWORD)]
        self.advapi.ConvertStringSecurityDescriptorToSecurityDescriptorW.restype = wintypes.BOOL
        self.advapi.GetSecurityDescriptorOwner.argtypes = [pointer, ctypes.POINTER(pointer),
                                                         ctypes.POINTER(wintypes.BOOL)]
        self.advapi.GetSecurityDescriptorOwner.restype = wintypes.BOOL
        self.advapi.GetSecurityDescriptorDacl.argtypes = [pointer, ctypes.POINTER(wintypes.BOOL),
                                                        ctypes.POINTER(pointer), ctypes.POINTER(wintypes.BOOL)]
        self.advapi.GetSecurityDescriptorDacl.restype = wintypes.BOOL
        self.advapi.SetSecurityInfo.argtypes = [wintypes.HANDLE, ctypes.c_int, wintypes.DWORD,
                                              pointer, pointer, pointer, pointer]
        self.advapi.SetSecurityInfo.restype = wintypes.DWORD

        class SecurityAttributes(ctypes.Structure):
            _fields_ = [('nLength', wintypes.DWORD), ('lpSecurityDescriptor', pointer),
                        ('bInheritHandle', wintypes.BOOL)]

        class FileInformation(ctypes.Structure):
            _fields_ = [('dwFileAttributes', wintypes.DWORD),
                        ('ftCreationTime', wintypes.FILETIME), ('ftLastAccessTime', wintypes.FILETIME),
                        ('ftLastWriteTime', wintypes.FILETIME), ('dwVolumeSerialNumber', wintypes.DWORD),
                        ('nFileSizeHigh', wintypes.DWORD), ('nFileSizeLow', wintypes.DWORD),
                        ('nNumberOfLinks', wintypes.DWORD), ('nFileIndexHigh', wintypes.DWORD),
                        ('nFileIndexLow', wintypes.DWORD)]

        self.SecurityAttributes = SecurityAttributes
        self.FileInformation = FileInformation
        self.kernel.CreateDirectoryW.argtypes = [wintypes.LPCWSTR, ctypes.POINTER(SecurityAttributes)]
        self.kernel.CreateDirectoryW.restype = wintypes.BOOL
        self.kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                                            ctypes.POINTER(SecurityAttributes), wintypes.DWORD,
                                            wintypes.DWORD, wintypes.HANDLE]
        self.kernel.CreateFileW.restype = wintypes.HANDLE
        self.kernel.GetFileInformationByHandle.argtypes = [wintypes.HANDLE, ctypes.POINTER(FileInformation)]
        self.kernel.GetFileInformationByHandle.restype = wintypes.BOOL

    @staticmethod
    def _failure() -> AccountProfilePermissionError:
        return AccountProfilePermissionError('无法建立仅当前用户和系统可访问的账号存储权限；操作已阻止。')

    def _user_sid(self) -> str:
        c, w = self.ctypes, self.wintypes
        token = w.HANDLE()
        if not self.advapi.OpenProcessToken(self.kernel.GetCurrentProcess(), 0x0008, c.byref(token)):
            raise self._failure()
        try:
            length = w.DWORD()
            self.advapi.GetTokenInformation(token, 1, None, 0, c.byref(length))
            if not length.value:
                raise self._failure()
            buffer = c.create_string_buffer(length.value)
            if not self.advapi.GetTokenInformation(token, 1, buffer, length, c.byref(length)):
                raise self._failure()
            sid = c.cast(buffer, c.POINTER(c.c_void_p))[0]
            text = w.LPWSTR()
            if not self.advapi.ConvertSidToStringSidW(sid, c.byref(text)):
                raise self._failure()
            try:
                return text.value
            finally:
                self.kernel.LocalFree(c.cast(text, c.c_void_p))
        finally:
            self.kernel.CloseHandle(token)

    def _check(self, handle, *, directory: bool) -> None:
        info = self.FileInformation()
        if not self.kernel.GetFileInformationByHandle(handle, self.ctypes.byref(info)):
            raise self._failure()
        # Never follow a junction/symlink into shared storage.
        if info.dwFileAttributes & 0x0400 or bool(info.dwFileAttributes & 0x0010) != directory:
            raise self._failure()

    def _protect(self, handle) -> None:
        # OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION |
        # PROTECTED_DACL_SECURITY_INFORMATION. Also reset a pre-existing owner:
        # an unrelated owner otherwise retains implicit permission to edit ACLs.
        if self.advapi.SetSecurityInfo(handle, 1, 0x80000005, self.owner, None, self.dacl, None) != 0:
            raise self._failure()

    def open(self) -> None:
        c, w = self.ctypes, self.wintypes
        sid = self._user_sid()
        sddl = f'O:{sid}D:P(A;OICI;FA;;;{sid})(A;OICI;FA;;;SY)'
        if not self.advapi.ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl, 1, c.byref(self.descriptor), None):
            raise self._failure()
        defaulted, present = w.BOOL(), w.BOOL()
        if (not self.advapi.GetSecurityDescriptorOwner(self.descriptor, c.byref(self.owner), c.byref(defaulted))
                or not self.advapi.GetSecurityDescriptorDacl(self.descriptor, c.byref(present),
                                                           c.byref(self.dacl), c.byref(defaulted))
                or not present.value or not self.dacl.value or not self.owner.value):
            raise self._failure()
        attributes = self.SecurityAttributes(c.sizeof(self.SecurityAttributes), self.descriptor, False)
        if not self.kernel.CreateDirectoryW(str(self.directory), c.byref(attributes)):
            if c.get_last_error() != 183:  # ERROR_ALREADY_EXISTS
                raise self._failure()
        # Keep the private directory open without FILE_SHARE_DELETE during the
        # entire operation, including atomic replacement. A writable parent must
        # not allow the private directory to be swapped during credential I/O.
        handle = self.kernel.CreateFileW(str(self.directory), 0x000E0080, 0x00000003,
                                         None, 3, 0x02200000, None)
        if handle == c.c_void_p(-1).value:
            raise self._failure()
        self.directory_handle = handle
        self._check(handle, directory=True)
        self._protect(handle)

    def open_file(self, name: str, *, create: bool = False) -> int | None:
        import msvcrt

        c = self.ctypes
        access = (0x40000000 if create else 0x80000000) | 0x000E0000
        # Newly created files inherit the already protected directory DACL.
        handle = self.kernel.CreateFileW(str(self.directory / name), access, 1, None,
                                         1 if create else 3, 0x00200080, None)
        if handle == c.c_void_p(-1).value:
            if not create and c.get_last_error() == 2:
                return None
            raise self._failure()
        try:
            self._check(handle, directory=False)
            self._protect(handle)
            descriptor = msvcrt.open_osfhandle(handle, (os.O_WRONLY if create else os.O_RDONLY) | os.O_BINARY)
        except BaseException:
            self.kernel.CloseHandle(handle)
            raise
        return descriptor

    def replace(self, source: str, destination: str) -> None:
        os.replace(self.directory / source, self.directory / destination)

    def unlink(self, name: str) -> None:
        (self.directory / name).unlink(missing_ok=True)

    def close(self) -> None:
        if self.directory_handle is not None:
            self.kernel.CloseHandle(self.directory_handle)
            self.directory_handle = None
        if self.descriptor.value:
            self.kernel.LocalFree(self.descriptor)
            self.descriptor = self.ctypes.c_void_p()


class _PosixSecurity:
    def __init__(self, directory: Path) -> None:
        self.directory = directory
        self.directory_fd: int | None = None

    @staticmethod
    def _failure() -> AccountProfilePermissionError:
        return AccountProfilePermissionError('无法建立仅当前用户可访问的账号存储权限；操作已阻止。')

    def open(self) -> None:
        try:
            os.mkdir(self.directory, 0o700)
        except FileExistsError:
            pass
        # Use directory-relative file operations so a writable parent cannot
        # redirect an operation by replacing the private directory pathname.
        self.directory_fd = os.open(self.directory, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        details = os.fstat(self.directory_fd)
        if not stat.S_ISDIR(details.st_mode) or details.st_uid != os.geteuid():
            raise self._failure()
        os.fchmod(self.directory_fd, 0o700)
        if stat.S_IMODE(os.fstat(self.directory_fd).st_mode) != 0o700:
            raise self._failure()

    def open_file(self, name: str, *, create: bool = False) -> int | None:
        flags = os.O_NOFOLLOW | (os.O_WRONLY | os.O_CREAT | os.O_EXCL if create else os.O_RDONLY)
        try:
            descriptor = os.open(name, flags, 0o600, dir_fd=self.directory_fd)
        except FileNotFoundError:
            if not create:
                return None
            raise
        try:
            details = os.fstat(descriptor)
            if not stat.S_ISREG(details.st_mode) or details.st_uid != os.geteuid():
                raise self._failure()
            os.fchmod(descriptor, 0o600)
            if stat.S_IMODE(os.fstat(descriptor).st_mode) != 0o600:
                raise self._failure()
        except BaseException:
            os.close(descriptor)
            raise
        return descriptor

    def replace(self, source: str, destination: str) -> None:
        os.replace(source, destination, src_dir_fd=self.directory_fd, dst_dir_fd=self.directory_fd)

    def unlink(self, name: str) -> None:
        try:
            os.unlink(name, dir_fd=self.directory_fd)
        except FileNotFoundError:
            pass

    def close(self) -> None:
        if self.directory_fd is not None:
            os.close(self.directory_fd)
            self.directory_fd = None


class AccountStore:
    """One serialized owner for the private profiles document.

    The DACL excludes other ordinary Windows users; system administrators and
    processes running as this user remain within the OS trust boundary. This is
    plaintext storage, not encryption. Cross-process updates are not merged.
    """

    SCHEMA_VERSION = 1
    new_profile = staticmethod(new_profile)
    public_view = staticmethod(public_view)
    validate_document = staticmethod(validate_document)

    def __init__(self, data_dir: Path) -> None:
        self.data_dir = Path(data_dir)
        self.private_dir = self.data_dir / 'account-profiles-private'
        self.path = self.private_dir / 'profiles.json'
        self._lock = threading.RLock()

    @contextmanager
    def _private_storage(self):
        security = None
        try:
            self.data_dir.mkdir(parents=True, exist_ok=True)
            security = _WindowsSecurity(self.private_dir) if os.name == 'nt' else _PosixSecurity(self.private_dir)
            security.open()
            yield security
        except AccountProfileError:
            raise
        except (OSError, AttributeError):
            raise AccountProfilePermissionError('无法安全访问账号私有存储；操作已阻止。') from None
        finally:
            if security is not None:
                security.close()

    @staticmethod
    def _read(security) -> dict | None:
        descriptor = security.open_file('profiles.json')
        if descriptor is None:
            return None
        with os.fdopen(descriptor, 'rb') as stream:
            raw = stream.read()
        return _decode(raw)

    def load(self) -> dict:
        """Load a strict document; missing files yield an empty document."""
        with self._lock, self._private_storage() as security:
            document = self._read(security)
            if document is None:
                return {'schema_version': 1, 'selected_ids': [], 'profiles': []}
            return document

    def save(self, document: object) -> None:
        """Validate and atomically write after establishing private permissions."""
        validated = validate_document(document)
        try:
            # ASCII escaping preserves even unusual password code points without
            # normalization, truncation, or a credential-bearing encoding error.
            payload = (json.dumps(validated, ensure_ascii=True, indent=2) + '\n').encode('ascii')
        except (ValueError, UnicodeError, RecursionError):
            raise AccountProfileError('账号配置无法序列化；未写入文件。') from None
        with self._lock, self._private_storage() as security:
            # Validate the current original on every save, including a caller
            # that skipped load(). An invalid original must never be replaced.
            self._read(security)
            temporary = '.profiles-' + uuid.uuid4().hex + '.tmp'
            created = False
            try:
                descriptor = security.open_file(temporary, create=True)
                created = True
                with os.fdopen(descriptor, 'wb') as stream:
                    stream.write(payload)
                    stream.flush()
                    os.fsync(stream.fileno())
                security.replace(temporary, 'profiles.json')
            finally:
                if created:
                    try:
                        security.unlink(temporary)
                    except OSError:
                        # Any interrupted temporary file still has the same
                        # private ACL as the final credential file.
                        pass
