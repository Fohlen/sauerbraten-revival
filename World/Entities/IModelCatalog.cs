namespace SauerbratenRevival.World.Entities;

/// <summary>
/// Provides model metadata and converted glTF paths by engine model name.
/// Decouples model resolution from project resources and files on disk.
/// </summary>
public interface IModelCatalog
{
    /// <summary>Resolve model metadata by engine model name, or null if unknown.</summary>
    ModelInfo? Get(string name);
}
