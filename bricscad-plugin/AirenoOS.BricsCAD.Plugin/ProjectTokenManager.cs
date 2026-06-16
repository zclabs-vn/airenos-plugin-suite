using Teigha.DatabaseServices;
using Teigha.Runtime;

namespace AirenoOS.BricsCAD.Plugin
{
    /// <summary>
    /// Manages the document-level project token stored in the Named Object Dictionary (NOD).
    /// Key: AIRENO_PROJECT_TOKEN
    /// Key: AIRENO_PATH_HASH  — used to detect file copy / Save As
    /// </summary>
    internal static class ProjectTokenManager
    {
        private const string AppName      = "AIRENO";
        private const string TokenKey     = "AIRENO_PROJECT_TOKEN";
        private const string PathHashKey  = "AIRENO_PATH_HASH";

        public static void EnsureProjectToken(Database db)
        {
            using var tr = db.TransactionManager.StartTransaction();
            var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForWrite);

            var currentPathHash = GetPathHash(db.Filename);

            // Check if token already exists
            if (nod.Contains(TokenKey))
            {
                var storedPathHash = GetStringFromNod(nod, tr, PathHashKey);

                // File copy detected — path hash mismatch → generate new token
                if (storedPathHash != currentPathHash)
                {
                    WriteStringToNod(nod, tr, TokenKey, Guid.NewGuid().ToString());
                    WriteStringToNod(nod, tr, PathHashKey, currentPathHash);
                }
            }
            else
            {
                // First connection — write new token
                WriteStringToNod(nod, tr, TokenKey, Guid.NewGuid().ToString());
                WriteStringToNod(nod, tr, PathHashKey, currentPathHash);
            }

            tr.Commit();
        }

        public static string GetProjectToken(Database db)
        {
            using var tr = db.TransactionManager.StartTransaction();
            var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
            return GetStringFromNod(nod, tr, TokenKey) ?? string.Empty;
        }

        private static string GetPathHash(string path)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(path.ToLowerInvariant());
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                var hex = BitConverter.ToString(hash).Replace("-", string.Empty);
                return hex.Substring(0, 16);
            }
        }

        private static string? GetStringFromNod(DBDictionary nod, Transaction tr, string key)
        {
            if (!nod.Contains(key)) return null;
            var xr = (Xrecord)tr.GetObject(nod.GetAt(key), OpenMode.ForRead);
            var rb = xr.Data;
            return rb?.AsArray().FirstOrDefault(tv => tv.TypeCode == (short)DxfCode.Text).Value as string;
        }

        private static void WriteStringToNod(DBDictionary nod, Transaction tr, string key, string value)
        {
            var rb = new ResultBuffer(new TypedValue((int)DxfCode.Text, value));
            if (nod.Contains(key))
            {
                var xr = (Xrecord)tr.GetObject(nod.GetAt(key), OpenMode.ForWrite);
                xr.Data = rb;
            }
            else
            {
                var xr = new Xrecord { Data = rb };
                nod.SetAt(key, xr);                    // attach to NOD first — gives it a database owner
                tr.AddNewlyCreatedDBObject(xr, true);  // then register with the transaction
            }
        }
    }
}
