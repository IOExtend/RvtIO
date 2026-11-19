namespace RvtIO.Scene;

/// <summary>Options of <see cref="RvtSceneReader"/>.</summary>
public sealed class RvtSceneOptions
{
    /// <summary>The key of the model in the scene (<c>SceneModel.Key</c>). With the <c>SourceId</c> of an element it identifies the
    /// element outside the scene, so it must stay the same for every conversion of one file. Null takes the file name without its
    /// directory (with the extension) when a path is read; a document passed as it is has no file name and needs this key.</summary>
    public string? ModelKey { get; set; }

    /// <summary>Includes straight model lines and reinforcement described by its centre lines.</summary>
    public bool IncludeModelLinesAndReinforcement { get; set; }
}
