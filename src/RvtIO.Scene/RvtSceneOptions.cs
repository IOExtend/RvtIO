namespace RvtIO.Scene;

/// <summary>Options of <see cref="RvtSceneReader"/>.</summary>
public sealed class RvtSceneOptions
{
    /// <summary>The key of the model in the scene (<c>SceneModel.Key</c>). With the <c>SourceId</c> of an element it identifies the
    /// element outside the scene, so it must stay the same for every conversion of one file. Null takes the file name without its
    /// directory (with the extension) when a path is read; a stream, or a document passed as it is, has no file name and needs this key.</summary>
    public string? ModelKey { get; set; }

    /// <summary>Receives file-read progress for <see cref="RvtSceneReader.Read(string, RvtSceneOptions?)"/>.
    /// Applies when the adapter opens the file. It is <see cref="RvtReaderOptions.Progress"/> of that read, with the same guarantees on
    /// order and thread; the last update, <c>complete</c>, ends the file read, and the meshes and the scene are built after it.</summary>
    public IProgress<RvtReadProgress>? ReadProgress { get; set; }

    /// <summary>Receives completed and total drawable element counts during mesh extraction, on the calling thread.
    /// Starts at zero and ends when the drawable elements are processed, before mapping them into the scene.</summary>
    public IProgress<(int Completed, int Total)>? MeshProgress { get; set; }

    /// <summary>Opens scratch storage for inflated partitions when the scene reader opens a file.
    /// The factory follows the contract of <see cref="RvtReaderOptions.CreatePartitionScratch"/>.</summary>
    public Func<Stream>? CreatePartitionScratch { get; set; }

    /// <summary>Includes straight model lines and reinforcement described by its centre lines.</summary>
    public bool IncludeModelLinesAndReinforcement { get; set; }

    /// <summary>Includes reference geometry for project grids and levels.</summary>
    public bool IncludeDatumGeometry { get; set; }
}
