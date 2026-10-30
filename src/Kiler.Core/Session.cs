using Kiler.Crypto;

namespace Kiler.Core;

/// <summary>
/// A signed-in user (M4.4, decided at the start of M4): the level keys that unlocked with their password and passed the check
/// of their level (<see cref="SignIn"/>), and the rights of their user file at the sign-in. What it reads is level 0 and the
/// levels of its keys, never the <c>level</c> of the file (design 7.8); <see cref="CanEdit"/> and <see cref="IsAdmin"/> give
/// the screens, and only the keys give the data. A lock disposes the keys (<see cref="Dispose"/>): a use under way ends with
/// its key, and a use after it is <see cref="SessionLockedException"/>, which the API answers as locked (core.md).
/// </summary>
public sealed class Session : IDisposable
{
    private readonly IReadOnlyDictionary<int, SecretKey> _keys;
    private volatile bool _locked;

    /// <summary>Kept for the two below, which a change of password changes together (M4.5a).</summary>
    private readonly Lock _gate = new();
    private int _userVersion;
    private bool _mustChangePassword;

    /// <param name="user">The user file of the sign-in.</param>
    /// <param name="userVersion">The version of the user file.</param>
    /// <param name="keys">The keys that verified, by level from 1; the session owns them.</param>
    internal Session(UserFile user, int userVersion, IReadOnlyDictionary<int, SecretKey> keys)
    {
        Account = user.Account;
        DisplayName = user.DisplayName;
        CanEdit = user.CanEdit;
        IsAdmin = user.IsAdmin;
        _userVersion = userVersion;
        _mustChangePassword = user.MustChangePassword;
        _keys = keys;
        Levels = [0, .. keys.Keys.Order()];
    }

    public string Account { get; }

    public string DisplayName { get; }

    public bool CanEdit { get; }

    public bool IsAdmin { get; }

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

    /// <summary>Whether a lock, a sign-out or the stop has disposed the keys.</summary>
    public bool IsLocked => _locked;

    /// <summary>Whether the session reads <paramref name="level"/>: level 0, or a level whose key it holds.</summary>
    public bool Reads(int level) => level == 0 || _keys.ContainsKey(level);

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
    /// The keys of the session locked with <paramref name="password"/> (<see cref="LevelKeys.Lock(IReadOnlyDictionary{int, SecretKey}, ReadOnlySpan{char})"/>),
    /// for a change of password (M4.5a). PBKDF2 runs once. A lock before or during it is <see cref="SessionLockedException"/>, as for <see cref="Use"/>.
    /// </summary>
    /// <exception cref="SessionLockedException">The session is locked.</exception>
    internal LockedKeys LockKeys(ReadOnlySpan<char> password)
    {
        if (_locked)
        {
            throw new SessionLockedException();
        }

        try
        {
            return LevelKeys.Lock(_keys, password);
        }
        // As in Use: Dispose sets the mark before it disposes a key.
        catch (ObjectDisposedException) when (_locked)
        {
            throw new SessionLockedException();
        }
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

    /// <summary>Locks the session: disposes its keys, which zeroes them. A use under way ends with its key first.</summary>
    public void Dispose()
    {
        _locked = true;
        foreach (var key in _keys.Values)
        {
            key.Dispose();
        }
    }
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
/// keys. A request reads <see cref="Session"/> once and uses what it got.
/// </summary>
public sealed class CurrentSession : IDisposable
{
    private readonly Lock _gate = new();
    private Session? _session;
    private bool _disposed;

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
        lock (_gate)
        {
            if (_disposed)
            {
                before = session;
            }
            else
            {
                before = _session;
                _session = session;
            }
        }

        before?.Dispose();
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
