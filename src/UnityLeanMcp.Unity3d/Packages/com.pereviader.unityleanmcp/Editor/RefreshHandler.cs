using UnityEditor;
using UnityLeanMcp;

namespace PereViader.UnityLeanMcp.Editor
{
    internal sealed class RefreshHandler : CompilationCommandHandlerBase
    {
        protected override string OperationKind => OperationKinds.Refresh;
        protected override string StatusWord => "REFRESHING";
        protected override void ExecuteCompilation() => AssetDatabase.Refresh();
    }
}
