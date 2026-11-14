using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AiUsageWidget.Core
{
    // Minimal read-only access to one SQLite value through the winsqlite3.dll that ships
    // with Windows 10/11 (used for Cursor's state.vscdb). Cursor keeps the database open,
    // so the wrapper only ever opens it read-only and treats SQLITE_BUSY as "no value this
    // time". Any failure — missing dll, missing or locked file, missing row, non-database
    // file — returns null, and the caller's value is a short string it reads and discards.
    public static class Sqlite
    {
        const int SqliteOk = 0;
        const int SqliteRow = 100;
        const int SqliteOpenReadOnly = 0x00000001;

        // SQLITE_TRANSIENT: sqlite copies the bound text before the call returns.
        static readonly IntPtr SqliteTransient = new IntPtr(-1);

        // The one API. table is a fixed identifier chosen in code (never user input); key is
        // bound as a parameter. The value may be stored as text or as a UTF-8 blob; both
        // come back as a string.
        public static string ReadValue(string dbPath, string table, string key)
        {
            if (string.IsNullOrEmpty(dbPath) || string.IsNullOrEmpty(table) ||
                string.IsNullOrEmpty(key)) return null;
            if (!File.Exists(dbPath)) return null;

            IntPtr db = IntPtr.Zero;
            IntPtr stmt = IntPtr.Zero;
            try
            {
                // sqlite3_open_v2 takes a UTF-8 file name, so the path is marshalled by hand.
                byte[] pathBytes = Encoding.UTF8.GetBytes(dbPath + '\0');
                if (sqlite3_open_v2(pathBytes, out db, SqliteOpenReadOnly, IntPtr.Zero) != SqliteOk
                    || db == IntPtr.Zero) return null;

                string sql = "SELECT value FROM " + table + " WHERE key = ? LIMIT 1";
                if (sqlite3_prepare16_v2(db, sql, -1, out stmt, IntPtr.Zero) != SqliteOk
                    || stmt == IntPtr.Zero) return null;
                if (sqlite3_bind_text16(stmt, 1, key, key.Length * 2, SqliteTransient) != SqliteOk)
                    return null;

                // SQLITE_ROW is the only success; SQLITE_BUSY (another connection holds the
                // write lock) and a missing row both read as "no value this time".
                if (sqlite3_step(stmt) != SqliteRow) return null;

                IntPtr text = sqlite3_column_text16(stmt, 0);
                if (text != IntPtr.Zero)
                {
                    string value = Marshal.PtrToStringUni(text);
                    if (!string.IsNullOrEmpty(value)) return value;
                }

                // Stored as a UTF-8 blob (or the text conversion came back empty): read the
                // exact bytes and decode them here.
                IntPtr blob = sqlite3_column_blob(stmt, 0);
                int bytes = sqlite3_column_bytes(stmt, 0);
                if (blob == IntPtr.Zero || bytes <= 0) return null;
                byte[] buffer = new byte[bytes];
                Marshal.Copy(blob, buffer, 0, bytes);
                string decoded = Encoding.UTF8.GetString(buffer).TrimEnd('\0');
                return decoded.Length > 0 ? decoded : null;
            }
            catch (DllNotFoundException)
            {
                return null;   // pre-Windows 10 or a stripped-down install
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
            catch (BadImageFormatException)
            {
                return null;   // 32-bit dll under the 64-bit process or the reverse
            }
            finally
            {
                if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
                if (db != IntPtr.Zero) sqlite3_close(db);
            }
        }

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

        [DllImport("winsqlite3.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
        static extern int sqlite3_prepare16_v2(IntPtr db, string sql, int nByte, out IntPtr stmt,
            IntPtr tail);

        [DllImport("winsqlite3.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
        static extern int sqlite3_bind_text16(IntPtr stmt, int index, string value, int bytes,
            IntPtr destructor);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int sqlite3_step(IntPtr stmt);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr sqlite3_column_text16(IntPtr stmt, int column);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr sqlite3_column_blob(IntPtr stmt, int column);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int sqlite3_column_bytes(IntPtr stmt, int column);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int sqlite3_finalize(IntPtr stmt);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int sqlite3_close(IntPtr db);
    }
}
