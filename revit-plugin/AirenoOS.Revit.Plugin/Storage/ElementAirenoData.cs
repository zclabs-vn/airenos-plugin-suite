using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using EsSchema = Autodesk.Revit.DB.ExtensibleStorage.Schema;

namespace AirenoOS.Revit.Plugin.Storage
{
    /// <summary>
    /// Per-element plugin data, attached via ExtensibleStorage.
    /// Element.UniqueId already provides a native stable identifier; this schema
    /// only stores the fields that AirenoOS writes back: backpack_id, identity_state,
    /// confirmed_label, confirmed_room_id, last_synced, previous_label.
    ///
    /// Reads are non-throwing and return defaults so the extractor can run on every
    /// element without per-element try/catch noise.
    /// </summary>
    internal static class ElementAirenoData
    {
        private static readonly Guid SchemaGuid = new("9c4b2f1e-8d3a-4e7b-95c1-3a8e5f2d7b40");
        private const string SchemaName = "AirenoOSElementData";

        private const string F_Backpack    = "BackpackId";
        private const string F_Identity    = "IdentityState";
        private const string F_Label       = "ConfirmedLabel";
        private const string F_Room        = "ConfirmedRoomId";
        private const string F_LastSynced  = "LastSynced";
        private const string F_PrevLabel   = "PreviousLabel";

        private static EsSchema? _schema;

        private static EsSchema GetOrCreateSchema()
        {
            if (_schema != null) return _schema;
            _schema = EsSchema.Lookup(SchemaGuid);
            if (_schema != null) return _schema;

            var b = new SchemaBuilder(SchemaGuid);
            b.SetSchemaName(SchemaName);
            b.SetVendorId("AIRENOS");
            b.SetReadAccessLevel(AccessLevel.Public);
            b.SetWriteAccessLevel(AccessLevel.Public);
            b.AddSimpleField(F_Backpack,    typeof(string));
            b.AddSimpleField(F_Identity,    typeof(string));
            b.AddSimpleField(F_Label,       typeof(string));
            b.AddSimpleField(F_Room,        typeof(string));
            b.AddSimpleField(F_LastSynced,  typeof(string));
            b.AddSimpleField(F_PrevLabel,   typeof(string));
            _schema = b.Finish();
            return _schema;
        }

        public static Snapshot Read(Element element)
        {
            try
            {
                var schema = GetOrCreateSchema();
                var entity = element.GetEntity(schema);
                if (entity == null || !entity.IsValid()) return Snapshot.Empty;
                return new Snapshot
                {
                    BackpackId     = SafeGet(entity, F_Backpack),
                    IdentityState  = SafeGet(entity, F_Identity),
                    ConfirmedLabel = SafeGet(entity, F_Label),
                    ConfirmedRoom  = SafeGet(entity, F_Room),
                    LastSynced     = SafeGet(entity, F_LastSynced),
                    PreviousLabel  = SafeGet(entity, F_PrevLabel),
                };
            }
            catch
            {
                return Snapshot.Empty;
            }
        }

        /// <summary>
        /// Caller MUST already be inside a Transaction. Idempotent — writing the same
        /// values twice produces the same on-disk state.
        /// </summary>
        public static void Write(Element element, Snapshot values)
        {
            var schema = GetOrCreateSchema();
            var entity = element.GetEntity(schema);
            if (entity == null || !entity.IsValid()) entity = new Entity(schema);

            entity.Set(F_Backpack,    values.BackpackId    ?? string.Empty);
            entity.Set(F_Identity,    values.IdentityState ?? string.Empty);
            entity.Set(F_Label,       values.ConfirmedLabel?? string.Empty);
            entity.Set(F_Room,        values.ConfirmedRoom ?? string.Empty);
            entity.Set(F_LastSynced,  values.LastSynced    ?? string.Empty);
            entity.Set(F_PrevLabel,   values.PreviousLabel ?? string.Empty);

            element.SetEntity(entity);
        }

        private static string SafeGet(Entity entity, string field)
        {
            try { return entity.Get<string>(field) ?? string.Empty; }
            catch { return string.Empty; }
        }

        internal struct Snapshot
        {
            public static readonly Snapshot Empty = new();

            public string? BackpackId;
            public string? IdentityState;
            public string? ConfirmedLabel;
            public string? ConfirmedRoom;
            public string? LastSynced;
            public string? PreviousLabel;
        }
    }
}
