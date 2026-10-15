using System.IO.Pipes;

namespace Sovereign.Desktop.Client;

/// <summary>
/// Named-pipe create and connect flags. Pipe names and frame bytes stay as they are.
/// On Windows, <see cref="PipeOptions.CurrentUserOnly"/> installs one allow rule for the current token owner and no one else.
/// <see cref="PipeOptions.FirstPipeInstance"/> makes create fail when that name already exists.
/// </summary>
public static class LocalPipeAccess
{
    public const PipeOptions Server =
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance;

    public const PipeOptions Client =
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;

    public const PipeOptions SynchronousClient = PipeOptions.CurrentUserOnly;

    public static bool RestrictsToCurrentUser(PipeOptions options) =>
        (options & PipeOptions.CurrentUserOnly) != 0;

    public static bool FailsIfNameExists(PipeOptions options) =>
        (options & PipeOptions.FirstPipeInstance) != 0;
}
