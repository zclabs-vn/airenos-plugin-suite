using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using EsSchema = Autodesk.Revit.DB.ExtensibleStorage.Schema;

namespace AirenoOS.Revit.Plugin.Storage
{
    /// <summary>
    /// Single ExtensibleStorage schema attached to ProjectInformation that holds every
    /// document-scoped plugin value: project token, path hash, endpoint, bearer token,
    /// and the writeback queue JSON.
    ///
    /// One schema (vs. one per concern) keeps the storage cost flat — Revit charges
    /// extra schema lookups, and we already hold a Transaction whenever we write,
    /// so there is no contention benefit to splitting.
    /// </summary>
    internal static class AirenoDataStore
    {
        // Stable schema GUID — must never change once shipped, or existing docs lose data.
        private static readonly Guid SchemaGuid = new("4f7e9a2c-1b3d-4c5e-8a6f-2d9b7c4e1a30");
        private const string SchemaName = "AirenoOSProjectData";

        private const string FieldToken    = "ProjectToken";
        private const string FieldPathHash = "PathHash";
        private const string FieldEndpoint = "Endpoint";
        private const string FieldBearer   = "BearerToken";
        private const string FieldQueue    = "WritebackQueueJson";

        private static EsSchema? _schema;

        private static EsSchema GetOrCreateSchema()
        {
            if (_schema != null) return _schema;
            _schema = EsSchema.Lookup(SchemaGuid);
            if (_schema != null) return _schema;

            // Vendor ID convention: ADSK reserves leading letters; "AIRENOS" is safe.
            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName(SchemaName);
            builder.SetVendorId("AIRENOS");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);

            builder.AddSimpleField(FieldToken,    typeof(string));
            builder.AddSimpleField(FieldPathHash, typeof(string));
            builder.AddSimpleField(FieldEndpoint, typeof(string));
            builder.AddSimpleField(FieldBearer,   typeof(string));
            builder.AddSimpleField(FieldQueue,    typeof(string));

            _schema = builder.Finish();
            return _schema;
        }

        private static Element Host(Document doc) => doc.ProjectInformation;

        /// <summary>
        /// Reads all fields in one pass. Missing values come back as empty strings —
        /// callers translate empties to nulls or defaults at the boundary.
        /// </summary>
        public static StoredValues Read(Document doc)
        {
            var schema = GetOrCreateSchema();
            var entity = Host(doc).GetEntity(schema);
            if (entity == null || !entity.IsValid())
                return new StoredValues();

            return new StoredValues
            {
                Token    = SafeGet(entity, FieldToken),
                PathHash = SafeGet(entity, FieldPathHash),
                Endpoint = SafeGet(entity, FieldEndpoint),
                Bearer   = SafeGet(entity, FieldBearer),
                Queue    = SafeGet(entity, FieldQueue),
            };
        }

        /// <summary>
        /// Writes a partial update. Caller MUST already be inside a Transaction.
        /// Null arguments leave the existing field untouched (read-modify-write).
        /// </summary>
        public static void Write(
            Document doc,
            string? token = null,
            string? pathHash = null,
            string? endpoint = null,
            string? bearer = null,
            string? queueJson = null)
        {
            var schema = GetOrCreateSchema();
            var host = Host(doc);

            var existing = host.GetEntity(schema);
            var entity = (existing != null && existing.IsValid()) ? existing : new Entity(schema);

            entity.Set(FieldToken,    token    ?? (existing != null && existing.IsValid() ? SafeGet(existing, FieldToken)    : string.Empty));
            entity.Set(FieldPathHash, pathHash ?? (existing != null && existing.IsValid() ? SafeGet(existing, FieldPathHash) : string.Empty));
            entity.Set(FieldEndpoint, endpoint ?? (existing != null && existing.IsValid() ? SafeGet(existing, FieldEndpoint) : string.Empty));
            entity.Set(FieldBearer,   bearer   ?? (existing != null && existing.IsValid() ? SafeGet(existing, FieldBearer)   : string.Empty));
            entity.Set(FieldQueue,    queueJson?? (existing != null && existing.IsValid() ? SafeGet(existing, FieldQueue)    : string.Empty));

            host.SetEntity(entity);
        }

        private static string SafeGet(Entity entity, string field)
        {
            try { return entity.Get<string>(field) ?? string.Empty; }
            catch { return string.Empty; }
        }

        internal struct StoredValues
        {
            public string Token;
            public string PathHash;
            public string Endpoint;
            public string Bearer;
            public string Queue;
        }
    }
}
