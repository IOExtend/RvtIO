namespace RvtIO.Model;

/// <summary>A public Extensible Storage entity and its saved field values.</summary>
public sealed record ExtensibleStorageEntity(Guid SchemaId, string SchemaName, IReadOnlyDictionary<string, object?> Fields);

/// <summary>Public entities of an element in a loaded family's saved document, identified by content key and saved element ID.</summary>
public sealed record EmbeddedFamilyElementStorage(Guid ContentKey, string ElementId, IReadOnlyList<ExtensibleStorageEntity> ExtensibleStorage);
