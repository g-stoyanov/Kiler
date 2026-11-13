using Kiler.Crypto;

namespace Kiler.Core;

/// <summary>
/// A signed-in user (M4.4, decided at the start of M4): the level keys that unlocked with their password and passed the check
/// of their level (<see cref="SignIn"/>), and the rights of their user file at the sign-in. What it reads is level 0 and the
/// levels of its keys, never the <c>level</c> of the file (design 7.8). A lock disposes the keys (<see cref="Dispose"/>): a
/// use under way ends with its key, and a use after it is <see cref="SessionLockedException"/>, which the API answers as
/// locked (core.md).
/// <para>
/// With the level keys, the same password unlocks the admin key of the user file (M4.13): its public key, which every user
/// has, and its private key, which an admin has (<see cref="AdminPublicKey"/>, <see cref="HoldsAdminKey"/>). The session
/// keeps them until a lock, as the level keys, and a change of password locks them again.
/// </para>
/// <para>
/// The rights come only from a user file whose seal verifies with <see cref="AdminPublicKey"/> (M4.15, docs/tasks/M4.md, "The
/// seal in the session"): from any other the session is a reader, and <see cref="RightsRefused"/> says why for the screen. The
/// admin right is also the private key that matches the public key, never <c>isAdmin</c> alone.
/// </para>
/// </summary>
public sealed class Session : IDisposable
{
    private readonly IReadOnlyDictionary<int, SecretKey> _keys;
    private readonly IReadOnlyDictionary<int, string> _checks;
    private readonly AdminKey? _adminKey;
    private volatile bool _locked;

    /// <summary>Kept for the two below, which a change of password changes together (M4.5a).</summary>
    private readonly Lock _gate = new();
    private int _userVersion;
    private bool _mustChangePassword;

    /// <param name="user">The user file of the sign-in.</param>
    /// <param name="userVersion">The version of the user file.</param>
    /// <param name="keys">The keys that verified, by level from 1; the session owns them.</param>
    /// <param name="checks">The check of <c>levels.json</c> that each key verified against, by level (M4.6).</param>
    /// <param name="adminPublicKey">The public key of the admin key that unlocked with the level keys, or <c>null</c> (M4.13).</param>
    /// <param name="adminKey">
    /// The admin key that unlocked with them, or <c>null</c>; the session owns it. One whose public key is not
    /// <paramref name="adminPublicKey"/> is not kept: it is disposed at once (M4.13).
    /// </param>
    internal Session(
        UserFile user,
        int userVersion,
        IReadOnlyDictionary<int, SecretKey> keys,
        IReadOnlyDictionary<int, string> checks,
        string? adminPublicKey = null,
        AdminKey? adminKey = null)
    {
        Account = user.Account;
        DisplayName = user.DisplayName;
        _userVersion = userVersion;
        _mustChangePassword = user.MustChangePassword;
        _keys = keys;
        _checks = checks;
        Levels = [0, .. keys.Keys.Order()];
        AdminPublicKey = adminPublicKey;
        if (adminKey is not null && !string.Equals(adminKey.PublicKey, adminPublicKey, StringComparison.Ordinal))
        {
            adminKey.Dispose();
            adminKey = null;
        }

        _adminKey = adminKey;

        // A right that someone wrote into the file is not sealed (M4.15), and an isAdmin without the private key seals nothing.
        var rightsSealed = adminPublicKey is not null && user.IsSealedBy(adminPublicKey);
        CanEdit = rightsSealed && user.CanEdit;
        IsAdmin = rightsSealed && user.IsAdmin && adminKey is not null;
        RightsRefused = !rightsSealed ? RightsRefusal.NotSealed
            : user.IsAdmin && adminKey is null ? RightsRefusal.NoAdminKey
            : null;
    }

    public string Account { get; }

    public string DisplayName { get; }

    /// <summary>Whether the user file has <c>canEdit</c>, sealed by the admin key of <see cref="AdminPublicKey"/> (M4.15).</summary>
    public bool CanEdit { get; }

    /// <summary>
    /// Whether the user file has <c>isAdmin</c>, sealed by the admin key of <see cref="AdminPublicKey"/>, and the session
    /// holds the private key of that admin key (<see cref="HoldsAdminKey"/>, M4.15).
    /// </summary>
    public bool IsAdmin { get; }

    /// <summary>
    /// Why the session does not get the rights that its user file says, for the screen; <c>null</c> when it gets them all
    /// (M4.15): a file whose seal does not verify gives no right, and a sealed <c>isAdmin</c> without the private key gives no
    /// admin right.
    /// </summary>
    public RightsRefusal? RightsRefused { get; }

    /// <summary>
    /// Whether the password of the sign-in is a temporary one: the session can then only change it, and the change clears the
    /// mark (<see cref="PasswordChange"/>, M4.5a).
    /// </summary>
    public bool MustChangePassword
    {
        get
        {
            lock (_gate)
            {
                return _mustChangePassword;
            }
        }
    }

    /// <summary>
    /// The version of the user file that the session knows: that of the sign-in, then that of its last change of password. A
    /// change of password refuses a user file at another version (decided by the user in M4.5).
    /// </summary>
    public int UserVersion
    {
        get
        {
            lock (_gate)
            {
                return _userVersion;
            }
        }
    }

    /// <summary>The levels that the session reads: 0, and the levels of its keys, in order.</summary>
    public IReadOnlyList<int> Levels { get; }

    /// <summary>
    /// The public key of the admin key (M3.9) that unlocked with the level keys at the sign-in, or <c>null</c> when the user
    /// file has none or it did not unlock (M4.13). It is no secret, so a lock leaves it.
    /// </summary>
    public string? AdminPublicKey { get; }

    /// <summary>
    /// Whether the session holds the admin key, the private key of <see cref="AdminPublicKey"/>, which only an admin's user
    /// file has (M4.13). A lock disposes it, as a level key.
    /// </summary>
    public bool HoldsAdminKey => _adminKey is not null;

    /// <summary>Whether a lock, a sign-out or the stop has disposed the keys.</summary>
    public bool IsLocked => _locked;

    /// <summary>Whether the session reads <paramref name="level"/>: level 0, or a level whose key it holds.</summary>
    public bool Reads(int level) => level == 0 || _keys.ContainsKey(level);

    /// <summary>
    /// Whether the levels 1 to the top level of <paramref name="levels"/> are those of the keys of the session, no more and no
    /// fewer, each verified at the sign-in against the check that <paramref name="levels"/> has now (ordinal). A check planted
    /// after the sign-in, which a key of the session would not verify, is not written back by an admin action under the
    /// admin's name (M4.6, the M4.4 security review), nor is a <c>levels.json</c> cut down below the keys of the session (M4.15,
    /// the M4.6 security review). The callers give the newest version that the public key of the session verifies
    /// (<see cref="TeamFiles.SealedLevels"/>, M4.16a), as the sign-in took it, so a session that signed in with the sealed
    /// version from the local copy holds every level of it while the share has another.
    /// </summary>
    public bool HoldsEvery(LevelsFile levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        return _checks.Count == levels.Top
            && Enumerable.Range(1, levels.Top).All(level => _checks.TryGetValue(level, out var check) && string.Equals(check, levels.Levels[level].Check, StringComparison.Ordinal));
    }

    /// <summary>
    /// Runs <paramref name="use"/> with the key of <paramref name="level"/>, which it must not keep. A lock before it or
    /// during it is <see cref="SessionLockedException"/>: a use of the key under way ends first (M3.1).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The session has no key of <paramref name="level"/>; the caller checks <see cref="Reads"/>.</exception>
    /// <exception cref="SessionLockedException">The session is locked.</exception>
    public T Use<T>(int level, Func<SecretKey, T> use)
    {
        ArgumentNullException.ThrowIfNull(use);
        if (!_keys.TryGetValue(level, out var key))
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "The session has no key of the level.");
        }

        if (_locked)
        {
            throw new SessionLockedException();
        }

        try
        {
            return use(key);
        }
        // Dispose sets the mark before it disposes a key, so a key that has gone is always a lock, not a fault of the code.
        catch (ObjectDisposedException) when (_locked)
        {
            throw new SessionLockedException();
        }
    }

    /// <summary>
    /// Runs <paramref name="use"/> with the admin key, which it must not keep, as <see cref="Use"/> does with a level key: a
    /// lock before it or during it is <see cref="SessionLockedException"/> (M4.13).
    /// </summary>
    /// <exception cref="InvalidOperationException">The session has no admin key; the caller checks <see cref="HoldsAdminKey"/>.</exception>
    /// <exception cref="SessionLockedException">The session is locked.</exception>
    internal T UseAdminKey<T>(Func<AdminKey, T> use)
    {
        ArgumentNullException.ThrowIfNull(use);
        if (_adminKey is not { } adminKey)
        {
            throw new InvalidOperationException("The session has no admin key.");
        }

        if (_locked)
        {
            throw new SessionLockedException();
        }

        try
        {
            return use(adminKey);
        }
        // As in Use: Dispose sets the mark before it disposes the key.
        catch (ObjectDisposedException) when (_locked)
        {
            throw new SessionLockedException();
        }
    }

    /// <summary>
    /// The keys of the session locked with <paramref name="password"/>, for a change of password (M4.5a): the level keys, with
    /// <see cref="AdminPublicKey"/> and the admin key when the session holds them (M4.13), through
    /// <see cref="LevelKeys.Lock(IReadOnlyDictionary{int, SecretKey}, string, AdminKey?, ReadOnlySpan{char})"/>, and without
    /// them through <see cref="LevelKeys.Lock(IReadOnlyDictionary{int, SecretKey}, ReadOnlySpan{char})"/>. PBKDF2 runs once.
    /// A lock before or during it is <see cref="SessionLockedException"/>, as for <see cref="Use"/>.
    /// </summary>
    /// <exception cref="SessionLockedException">The session is locked.</exception>
    internal LockedKeys LockKeys(ReadOnlySpan<char> password) => Lock(_keys, AdminPublicKey, _adminKey, password);

    /// <summary>
    /// The keys of the levels 1 to <paramref name="top"/> locked with <paramref name="password"/>, for an admin action that
    /// gives them to a user with a temporary password (M4.6, design 7.6): with <see cref="AdminPublicKey"/>, which every user
    /// gets, and with the admin key when <paramref name="withAdminKey"/>, for an admin (M4.14). PBKDF2 runs once. A lock
    /// before or during it is <see cref="SessionLockedException"/>, as for <see cref="Use"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The session has no key of a level up to <paramref name="top"/>; the caller checks <see cref="HoldsEvery"/>.</exception>
    /// <exception cref="InvalidOperationException">The session has no admin key; the caller checks <see cref="HoldsAdminKey"/>.</exception>
    /// <exception cref="SessionLockedException">The session is locked.</exception>
    internal LockedKeys LockKeys(int top, bool withAdminKey, ReadOnlySpan<char> password)
    {
        if (!Enumerable.Range(1, top).All(_keys.ContainsKey))
        {
            throw new ArgumentOutOfRangeException(nameof(top), top, "The session has no key of a level up to it.");
        }

        if (_adminKey is null)
        {
            throw new InvalidOperationException("The session has no admin key.");
        }

        return Lock(_keys.Where(key => key.Key <= top).ToDictionary(), AdminPublicKey, withAdminKey ? _adminKey : null, password);
    }

    /// <summary>A change of password wrote the user file at <paramref name="userVersion"/>: the password is no longer a temporary one.</summary>
    internal void PasswordChanged(int userVersion)
    {
        lock (_gate)
        {
            _userVersion = userVersion;
            _mustChangePassword = false;
        }
    }

    private LockedKeys Lock(IReadOnlyDictionary<int, SecretKey> keys, string? adminPublicKey, AdminKey? adminKey, ReadOnlySpan<char> password)
    {
        if (_locked)
        {
            throw new SessionLockedException();
        }

        try
        {
            return adminPublicKey is null ? LevelKeys.Lock(keys, password) : LevelKeys.Lock(keys, adminPublicKey, adminKey, password);
        }
        // As in Use: Dispose sets the mark before it disposes a key.
        catch (ObjectDisposedException) when (_locked)
        {
            throw new SessionLockedException();
        }
    }

    /// <summary>Locks the session: disposes its keys and its admin key, which zeroes them. A use under way ends with its key first.</summary>
    public void Dispose()
    {
        _locked = true;
        foreach (var key in _keys.Values)
        {
            key.Dispose();
        }

        _adminKey?.Dispose();
    }
}

/// <summary>Why a session does not get the rights that its user file says (<see cref="Session.RightsRefused"/>, M4.15).</summary>
public enum RightsRefusal
{
    /// <summary>
    /// The seal of the user file does not verify with the public key of the session: someone changed its rights, or wrote it
    /// whole, outside Kiler, so the session is a reader. An admin gives a new temporary password, which seals it again.
    /// </summary>
    NotSealed,

    /// <summary>
    /// The user file has a sealed <c>isAdmin</c>, and the private key of the admin key is missing or did not unlock, so the
    /// session is no admin. Another admin gives a new temporary password.
    /// </summary>
    NoAdminKey,
}

/// <summary>The session was locked before or while a request used its keys (<see cref="Session.Use"/>): the request answers as locked.</summary>
public sealed class SessionLockedException : Exception
{
    public SessionLockedException()
        : base("The session is locked.")
    {
    }

    public SessionLockedException(string message)
        : base(message)
    {
    }

    public SessionLockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The session of this Kiler (decided at the start of M4): one per process, as a Kiler runs for one Windows account, so all
/// its pages share it. A sign-in puts a new one in place and locks the one before; a lock, a sign-out and the stop dispose the
/// keys. A request reads <see cref="Session"/> once and uses what it got. Each sign-in and each lock raise
/// <see cref="Changed"/>, which every open page gets as the <c>session</c> event (M4.5b).
/// </summary>
public sealed class CurrentSession : IDisposable
{
    private readonly Lock _gate = new();
    private Session? _session;
    private bool _disposed;

    /// <summary>
    /// Raised after a sign-in (<see cref="Start"/>) and after a lock, once the keys are disposed, on the thread of the change:
    /// every page then drops what it shows and reads the session again. <see cref="Lock()"/> raises it also when there is no
    /// session, as a page that asks for the lock wants every page on the lock screen. A handler does not throw and keeps short.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>The session; <c>null</c> before a sign-in, after a lock and after the stop.</summary>
    public Session? Session
    {
        get
        {
            lock (_gate)
            {
                return _session;
            }
        }
    }

    /// <summary>
    /// Puts <paramref name="session"/> in place of the one there, which it locks. After the stop it locks
    /// <paramref name="session"/> instead, so a sign-in that ends after the stop keeps no keys.
    /// </summary>
    public void Start(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session? before;
        bool started;
        lock (_gate)
        {
            started = !_disposed;
            if (started)
            {
                before = _session;
                _session = session;
            }
            else
            {
                before = session;
            }
        }

        before?.Dispose();
        if (started)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Locks the session, the sign-out and the "Lock" button alike: the keys are disposed, and the next request has no session.</summary>
    public void Lock()
    {
        Session? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }

        session?.Dispose();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Locks <paramref name="session"/> only while it is still the one in place, for the automatic lock (<see cref="AutoLock"/>),
    /// so a sign-in that has put another session in place meanwhile keeps it.
    /// </summary>
    public void Lock(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            if (_session != session)
            {
                return;
            }

            _session = null;
        }

        session.Dispose();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Locks the session at the stop, and every session that a sign-in brings after it.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        Lock();
    }
}
