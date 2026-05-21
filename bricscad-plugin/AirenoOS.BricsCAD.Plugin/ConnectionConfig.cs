using Teigha.DatabaseServices;
using Teigha.Runtime;

namespace AirenoOS.BricsCAD.Plugin
{
    /// <summary>
    /// Stores endpoint URL and bearer token in the Named Object Dictionary.
    /// </summary>
    internal static class ConnectionConfig
    {
        private const string EndpointKey = "AIRENO_ENDPOINT";
        private const string TokenKey    = "AIRENO_TOKEN";

        public static void Save(Database db, string endpoint, string token)
        {
            using var tr = db.TransactionManager.StartTransaction();
            var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForWrite);
            WriteString(nod, tr, EndpointKey, endpoint);
            WriteString(nod, tr, TokenKey, token);
            tr.Commit();
        }

        public static (string Endpoint, string Token) Load(Database db)
        {
            using var tr = db.TransactionManager.StartTransaction();
            var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
            var endpoint = ReadString(nod, tr, EndpointKey) ?? "https://mcp.airenos.io/v1/extract";
            var token    = ReadString(nod, tr, TokenKey) ?? string.Empty;
            return (endpoint, token);
        }

        private static string? ReadString(DBDictionary nod, Transaction tr, string key)
        {
            if (!nod.Contains(key)) return null;
            var xr = (Xrecord)tr.GetObject(nod.GetAt(key), OpenMode.ForRead);
            return xr.Data?.AsArray().FirstOrDefault(tv => tv.TypeCode == (short)DxfCode.Text).Value as string;
        }

        private static void WriteString(DBDictionary nod, Transaction tr, string key, string value)
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
                nod.SetAt(key, xr);                    // attach to NOD first
                tr.AddNewlyCreatedDBObject(xr, true);  // then register with the transaction
            }
        }
    }
}
