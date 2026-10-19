using System.Runtime.InteropServices;

namespace ScreenTime;

/// <summary>
/// Minimal wrapper over winsqlite3.dll, the SQLite engine that ships with Windows 10/11.
/// No NuGet packages or bundled native binaries needed.
/// </summary>
internal static class SqliteNative
{
    private const string Lib = "winsqlite3.dll";

    [DllImport(Lib, CharSet = CharSet.Unicode)] public static extern int sqlite3_open16(string filename, out IntPtr db);
    [DllImport(Lib)] public static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Lib)] public static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport(Lib)] public static extern IntPtr sqlite3_errmsg16(IntPtr db);
    [DllImport(Lib)] public static extern long sqlite3_last_insert_rowid(IntPtr db);
    [DllImport(Lib, CharSet = CharSet.Unicode)] public static extern int sqlite3_prepare16_v2(IntPtr db, string sql, int nByte, out IntPtr stmt, IntPtr tail);
    [DllImport(Lib)] public static extern int sqlite3_step(IntPtr stmt);
    [DllImport(Lib)] public static extern int sqlite3_reset(IntPtr stmt);
    [DllImport(Lib)] public static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport(Lib)] public static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);
    [DllImport(Lib, CharSet = CharSet.Unicode)] public static extern int sqlite3_bind_text16(IntPtr stmt, int index, string value, int nBytes, IntPtr destructor);
    [DllImport(Lib)] public static extern int sqlite3_bind_null(IntPtr stmt, int index);
    [DllImport(Lib)] public static extern long sqlite3_column_int64(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern IntPtr sqlite3_column_text16(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern int sqlite3_column_type(IntPtr stmt, int col);
}

internal sealed class SqliteDb : IDisposable
{
    private IntPtr _h;

    public SqliteDb(string path)
    {
        int rc = SqliteNative.sqlite3_open16(path, out _h);
        if (rc != 0) throw new InvalidOperationException("Cannot open database: " + Error());
        SqliteNative.sqlite3_busy_timeout(_h, 5000);
    }

    public string Error() => _h == IntPtr.Zero ? "?" : Marshal.PtrToStringUni(SqliteNative.sqlite3_errmsg16(_h));

    public long LastInsertId => SqliteNative.sqlite3_last_insert_rowid(_h);

    public Stmt Prepare(string sql)
    {
        int rc = SqliteNative.sqlite3_prepare16_v2(_h, sql, -1, out IntPtr s, IntPtr.Zero);
        if (rc != 0) throw new InvalidOperationException($"SQL error ({rc}): {Error()} in: {sql}");
        return new Stmt(this, s);
    }

    /// <summary>Runs one statement, discarding any rows.</summary>
    public void Exec(string sql)
    {
        using var s = Prepare(sql);
        while (s.Step()) { }
    }

    public void Dispose()
    {
        if (_h != IntPtr.Zero) { SqliteNative.sqlite3_close_v2(_h); _h = IntPtr.Zero; }
    }
}

internal sealed class Stmt : IDisposable
{
    private static readonly IntPtr Transient = new(-1);
    private readonly SqliteDb _db;
    private IntPtr _h;

    public Stmt(SqliteDb db, IntPtr h) { _db = db; _h = h; }

    public Stmt Bind(int i, long v) { SqliteNative.sqlite3_bind_int64(_h, i, v); return this; }

    public Stmt Bind(int i, string v)
    {
        if (v == null) SqliteNative.sqlite3_bind_null(_h, i);
        else SqliteNative.sqlite3_bind_text16(_h, i, v, -1, Transient);
        return this;
    }

    public bool Step()
    {
        int rc = SqliteNative.sqlite3_step(_h);
        if (rc == 100) return true;   // SQLITE_ROW
        if (rc == 101) return false;  // SQLITE_DONE
        string err = _db.Error();
        SqliteNative.sqlite3_reset(_h);
        throw new InvalidOperationException($"SQLite step failed ({rc}): {err}");
    }

    public void Run() { Step(); Reset(); }

    public void Reset() => SqliteNative.sqlite3_reset(_h);

    public long Long(int col) => SqliteNative.sqlite3_column_int64(_h, col);

    public string Text(int col) =>
        SqliteNative.sqlite3_column_type(_h, col) == 5 ? null : Marshal.PtrToStringUni(SqliteNative.sqlite3_column_text16(_h, col));

    public void Dispose()
    {
        if (_h != IntPtr.Zero) { SqliteNative.sqlite3_finalize(_h); _h = IntPtr.Zero; }
    }
}
