using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityLeanMcp
{
    public readonly struct RoslynSupportStatus : IEquatable<RoslynSupportStatus>
    {
        public bool IsSupported { get; }
        public string UnsupportedReason { get; }

        public RoslynSupportStatus(bool isSupported, string unsupportedReason)
        {
            IsSupported = isSupported;
            UnsupportedReason = unsupportedReason;
        }

        public bool Equals(RoslynSupportStatus other)
        {
            return IsSupported == other.IsSupported
                && string.Equals(UnsupportedReason, other.UnsupportedReason, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is RoslynSupportStatus other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = (hash * 31) + IsSupported.GetHashCode();
                hash = (hash * 31) + (UnsupportedReason != null ? StringComparer.Ordinal.GetHashCode(UnsupportedReason) : 0);
                return hash;
            }
        }

        public static bool operator ==(RoslynSupportStatus left, RoslynSupportStatus right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(RoslynSupportStatus left, RoslynSupportStatus right)
        {
            return !left.Equals(right);
        }
    }

    internal static class RoslynCompilerHelper
    {
        private static bool s_Initialized;
        private static bool s_IsSupported;
        private static string s_UnsupportedReason = "";

        private static Assembly s_CodeAnalysisAsm;
        private static Assembly s_CSharpAsm;

        private static MethodInfo s_ParseTextMethod;
        private static MethodInfo s_CreateFromFileMethod;
        private static MethodInfo s_CreateCompMethod;
        private static MethodInfo s_EmitMethod;

        private static Type s_CompilationType;
        private static Type s_CompilationOptionsType;
        private static Type s_SyntaxTreeType;
        private static Type s_MetadataRefType;
        private static object s_CompilationOptions;

        private static List<object> s_CachedMetadataReferences;
        private static readonly object s_Lock = new object();

        private sealed class InitializationState
        {
            public bool IsSupported;
            public string UnsupportedReason = "";

            public Assembly CodeAnalysisAssembly;
            public Assembly CSharpAssembly;

            public MethodInfo ParseTextMethod;
            public MethodInfo CreateFromFileMethod;
            public MethodInfo CreateCompilationMethod;
            public MethodInfo EmitMethod;

            public Type CompilationType;
            public Type CompilationOptionsType;
            public Type SyntaxTreeType;
            public Type MetadataReferenceType;
            public object CompilationOptions;

            public List<object> CachedMetadataReferences;
        }

        public static RoslynSupportStatus GetSupportStatus()
        {
            EnsureInitialized();
            lock (s_Lock)
            {
                return new RoslynSupportStatus(s_IsSupported, s_UnsupportedReason);
            }
        }

        public static bool IsSupported => GetSupportStatus().IsSupported;

        public static string UnsupportedReason => GetSupportStatus().UnsupportedReason ?? "";

        internal static void EnsureInitialized()
        {
            if (Volatile.Read(ref s_Initialized)) return;

            lock (s_Lock)
            {
                if (Volatile.Read(ref s_Initialized)) return;

                // Build the complete reflection graph in private state. Publishing any of
                // these fields before the graph is complete allows another thread to observe
                // a partially initialized compiler, while setting s_Initialized first makes a
                // transient failure permanent for the lifetime of the domain.
                InitializationState state = TryInitialize();
                if (!state.IsSupported)
                {
                    s_IsSupported = false;
                    s_UnsupportedReason = state.UnsupportedReason;
                    // Deliberately leave s_Initialized false so a later access can retry
                    // after Unity finishes installing or loading its Roslyn assemblies.
                    return;
                }

                s_CodeAnalysisAsm = state.CodeAnalysisAssembly;
                s_CSharpAsm = state.CSharpAssembly;
                s_ParseTextMethod = state.ParseTextMethod;
                s_CreateFromFileMethod = state.CreateFromFileMethod;
                s_CreateCompMethod = state.CreateCompilationMethod;
                s_EmitMethod = state.EmitMethod;
                s_CompilationType = state.CompilationType;
                s_CompilationOptionsType = state.CompilationOptionsType;
                s_SyntaxTreeType = state.SyntaxTreeType;
                s_MetadataRefType = state.MetadataReferenceType;
                s_CompilationOptions = state.CompilationOptions;
                s_CachedMetadataReferences = state.CachedMetadataReferences;
                s_IsSupported = true;
                s_UnsupportedReason = "";

                // This is the release publication point for every field above.
                Volatile.Write(ref s_Initialized, true);
            }
        }

        private static InitializationState TryInitialize()
        {
            var state = new InitializationState();

            try
            {
                string dataPath = EditorApplication.applicationContentsPath;
                if (string.IsNullOrEmpty(dataPath) || !Directory.Exists(dataPath))
                {
                    state.UnsupportedReason = "EditorApplication.applicationContentsPath is invalid or does not exist.";
                    return state;
                }

                string[] candidateDirs = new[]
                {
                    Path.Combine(dataPath, "MonoBleedingEdge", "lib", "mono", "msbuild", "Current", "bin", "Roslyn"),
                    Path.Combine(dataPath, "DotNetSdkRoslyn"),
                    Path.Combine(dataPath, "Tools", "Roslyn"),
                    Path.Combine(dataPath, "MonoBleedingEdge", "lib", "mono", "4.5"),
                    Path.Combine(dataPath, "Frameworks", "MonoBleedingEdge", "lib", "mono", "msbuild", "Current", "bin", "Roslyn")
                };

                string roslynDir = null;
                foreach (var dir in candidateDirs)
                {
                    if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "Microsoft.CodeAnalysis.CSharp.dll")))
                    {
                        try
                        {
                            var testAsm = Assembly.LoadFrom(Path.Combine(dir, "Microsoft.CodeAnalysis.CSharp.dll"));
                            if (testAsm != null)
                            {
                                roslynDir = dir;
                                break;
                            }
                        }
                        catch { }
                    }
                }

                if (roslynDir == null)
                {
                    state.UnsupportedReason = "Roslyn compiler assemblies (Microsoft.CodeAnalysis.CSharp.dll) could not be found or loaded in the Unity Editor installation.";
                    return state;
                }

                string immutablePath = Path.Combine(roslynDir, "System.Collections.Immutable.dll");
                if (File.Exists(immutablePath))
                {
                    try { Assembly.LoadFrom(immutablePath); } catch { }
                }

                string metadataPath = Path.Combine(roslynDir, "System.Reflection.Metadata.dll");
                if (File.Exists(metadataPath))
                {
                    try { Assembly.LoadFrom(metadataPath); } catch { }
                }

                state.CodeAnalysisAssembly = Assembly.LoadFrom(Path.Combine(roslynDir, "Microsoft.CodeAnalysis.dll"));
                state.CSharpAssembly = Assembly.LoadFrom(Path.Combine(roslynDir, "Microsoft.CodeAnalysis.CSharp.dll"));

                if (state.CodeAnalysisAssembly == null || state.CSharpAssembly == null)
                {
                    state.UnsupportedReason = "Failed to load Microsoft.CodeAnalysis or Microsoft.CodeAnalysis.CSharp assemblies.";
                    return state;
                }

                state.SyntaxTreeType = state.CSharpAssembly.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree");
                state.CompilationType = state.CSharpAssembly.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation");
                state.CompilationOptionsType = state.CSharpAssembly.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions");
                state.MetadataReferenceType = state.CodeAnalysisAssembly.GetType("Microsoft.CodeAnalysis.MetadataReference");
                var outputKindEnum = state.CodeAnalysisAssembly.GetType("Microsoft.CodeAnalysis.OutputKind");

                if (state.SyntaxTreeType == null || state.CompilationType == null || state.CompilationOptionsType == null || state.MetadataReferenceType == null || outputKindEnum == null)
                {
                    state.UnsupportedReason = "Failed to resolve required Roslyn reflection types.";
                    return state;
                }

                foreach (var m in state.SyntaxTreeType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name == "ParseText" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(string))
                    {
                        state.ParseTextMethod = m;
                        break;
                    }
                }

                foreach (var m in state.MetadataReferenceType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name == "CreateFromFile" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(string))
                    {
                        if (state.CreateFromFileMethod == null || m.GetParameters().Length < state.CreateFromFileMethod.GetParameters().Length)
                        {
                            state.CreateFromFileMethod = m;
                        }
                    }
                }

                object outputKindDynamicallyLinkedLibrary = Enum.Parse(outputKindEnum, "DynamicallyLinkedLibrary");
                var ctors = state.CompilationOptionsType.GetConstructors();
                foreach (var ctor in ctors)
                {
                    var pars = ctor.GetParameters();
                    if (pars.Length >= 1 && pars[0].ParameterType == outputKindEnum)
                    {
                        var args = new object[pars.Length];
                        args[0] = outputKindDynamicallyLinkedLibrary;
                        for (int i = 1; i < pars.Length; i++)
                        {
                            args[i] = pars[i].DefaultValue != DBNull.Value ? pars[i].DefaultValue : null;
                        }
                        try
                        {
                            state.CompilationOptions = ctor.Invoke(args);
                            break;
                        }
                        catch { }
                    }
                }

                foreach (var m in state.CompilationType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name == "Create" && m.GetParameters().Length == 4)
                    {
                        var p = m.GetParameters();
                        if (p[0].ParameterType == typeof(string) && p[3].ParameterType == state.CompilationOptionsType)
                        {
                            state.CreateCompilationMethod = m;
                            break;
                        }
                    }
                }

                foreach (var m in state.CompilationType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name == "Emit" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(Stream))
                    {
                        if (state.EmitMethod == null || m.GetParameters().Length == 1)
                        {
                            state.EmitMethod = m;
                            if (m.GetParameters().Length == 1) break;
                        }
                    }
                }

                if (state.ParseTextMethod == null || state.CreateFromFileMethod == null || state.CompilationOptions == null || state.CreateCompilationMethod == null || state.EmitMethod == null)
                {
                    state.UnsupportedReason = "Could not bind all required Roslyn methods.";
                    return state;
                }

                state.CachedMetadataReferences = BuildMetadataReferences(state.CreateFromFileMethod);
                state.IsSupported = true;
                return state;
            }
            catch (Exception ex)
            {
                state.UnsupportedReason = "Exception initializing Roslyn compiler: " + ex.Message;
                return state;
            }
        }

        private static List<object> BuildMetadataReferences(MethodInfo createFromFileMethod)
        {
            var refList = new List<object>();
            var addedLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var addedAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddRef(string path, string assemblyName = null)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                if (!addedLocations.Add(path)) return;

                if (!string.IsNullOrEmpty(assemblyName))
                {
                    if (!addedAssemblyNames.Add(assemblyName)) return;
                }

                try
                {
                    object r;
                    var pars = createFromFileMethod.GetParameters();
                    if (pars.Length == 1)
                    {
                        r = createFromFileMethod.Invoke(null, new object[] { path });
                    }
                    else
                    {
                        var args = new object[pars.Length];
                        args[0] = path;
                        for (int i = 1; i < pars.Length; i++)
                        {
                            args[i] = pars[i].DefaultValue != DBNull.Value ? pars[i].DefaultValue : null;
                        }
                        r = createFromFileMethod.Invoke(null, args);
                    }

                    if (r != null)
                    {
                        refList.Add(r);
                    }
                }
                catch { }
            }

            void AddAssembly(Assembly asm)
            {
                if (asm == null || asm.IsDynamic) return;
                try
                {
                    string loc = asm.Location;
                    if (string.IsNullOrEmpty(loc) || !File.Exists(loc)) return;
                    string name = asm.GetName().Name;
                    AddRef(loc, name);
                }
                catch { }
            }

            // 1. All currently loaded assemblies in AppDomain
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                AddAssembly(asm);
            }

            // 2. Core framework types
            try { AddAssembly(typeof(object).Assembly); } catch { }
            try { AddAssembly(typeof(System.Linq.Enumerable).Assembly); } catch { }
            try { AddAssembly(typeof(System.Collections.Generic.List<>).Assembly); } catch { }
            try { AddAssembly(typeof(UnityEngine.Object).Assembly); } catch { }
            try { AddAssembly(typeof(UnityEngine.GameObject).Assembly); } catch { }
            try { AddAssembly(typeof(UnityEditor.Editor).Assembly); } catch { }

            return refList;
        }

        public static bool CompileAndEmit(string sourceCode, out byte[] assemblyBytes, out List<string> errors)
        {
            assemblyBytes = null;
            errors = new List<string>();

            if (!IsSupported)
            {
                errors.Add(UnsupportedReason);
                return false;
            }

            try
            {
                // 1. Parse SyntaxTree
                object syntaxTree;
                var parsePars = s_ParseTextMethod.GetParameters();
                if (parsePars.Length == 1)
                {
                    syntaxTree = s_ParseTextMethod.Invoke(null, new object[] { sourceCode });
                }
                else
                {
                    var parseArgs = new object[parsePars.Length];
                    parseArgs[0] = sourceCode;
                    for (int i = 1; i < parsePars.Length; i++)
                    {
                        parseArgs[i] = parsePars[i].DefaultValue != DBNull.Value ? parsePars[i].DefaultValue : null;
                    }
                    syntaxTree = s_ParseTextMethod.Invoke(null, parseArgs);
                }

                // 2. SyntaxTree array
                var syntaxTreeArray = Array.CreateInstance(s_CodeAnalysisAsm.GetType("Microsoft.CodeAnalysis.SyntaxTree"), 1);
                syntaxTreeArray.SetValue(syntaxTree, 0);

                // 3. Metadata references array
                var refArray = Array.CreateInstance(s_MetadataRefType, s_CachedMetadataReferences.Count);
                for (int i = 0; i < s_CachedMetadataReferences.Count; i++)
                {
                    refArray.SetValue(s_CachedMetadataReferences[i], i);
                }

                // 4. Create compilation
                string assemblyName = "__UnityLeanMcpEval_" + Guid.NewGuid().ToString("N");
                var compilation = s_CreateCompMethod.Invoke(null, new object[] { assemblyName, syntaxTreeArray, refArray, s_CompilationOptions });

                // 5. Emit to memory stream
                using (var ms = new MemoryStream())
                {
                    object emitResult;
                    if (s_EmitMethod.GetParameters().Length == 1)
                    {
                        emitResult = s_EmitMethod.Invoke(compilation, new object[] { ms });
                    }
                    else
                    {
                        var emitPars = s_EmitMethod.GetParameters();
                        var emitArgs = new object[emitPars.Length];
                        emitArgs[0] = ms;
                        for (int i = 1; i < emitPars.Length; i++)
                        {
                            emitArgs[i] = emitPars[i].DefaultValue != DBNull.Value ? emitPars[i].DefaultValue : null;
                        }
                        emitResult = s_EmitMethod.Invoke(compilation, emitArgs);
                    }

                    var successProp = emitResult.GetType().GetProperty("Success");
                    bool isSuccess = (bool)successProp.GetValue(emitResult);

                    if (!isSuccess)
                    {
                        var diagsProp = emitResult.GetType().GetProperty("Diagnostics");
                        var diags = (System.Collections.IEnumerable)diagsProp.GetValue(emitResult);
                        foreach (var d in diags)
                        {
                            if (d != null)
                            {
                                errors.Add(d.ToString());
                            }
                        }
                        return false;
                    }

                    ms.Seek(0, SeekOrigin.Begin);
                    assemblyBytes = ms.ToArray();
                    return true;
                }
            }
            catch (Exception ex)
            {
                errors.Add("Roslyn compilation error: " + ex.Message);
                return false;
            }
        }

        private static object ParseSyntaxTree(string sourceCode)
        {
            if (s_ParseTextMethod == null) return null;
            var parsePars = s_ParseTextMethod.GetParameters();
            if (parsePars.Length == 1)
            {
                return s_ParseTextMethod.Invoke(null, new object[] { sourceCode });
            }
            var parseArgs = new object[parsePars.Length];
            parseArgs[0] = sourceCode;
            for (int i = 1; i < parsePars.Length; i++)
            {
                parseArgs[i] = parsePars[i].DefaultValue != DBNull.Value
                    ? parsePars[i].DefaultValue
                    : (parsePars[i].ParameterType.IsValueType ? Activator.CreateInstance(parsePars[i].ParameterType) : null);
            }
            return s_ParseTextMethod.Invoke(null, parseArgs);
        }

        private static object GetSyntaxTreeRoot(object syntaxTree)
        {
            if (syntaxTree == null) return null;
            MethodInfo getRootMethod = syntaxTree.GetType().GetMethod("GetRoot", new Type[] { typeof(CancellationToken) });
            if (getRootMethod == null)
            {
                foreach (var m in syntaxTree.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name == "GetRoot" && m.GetParameters().Length <= 1)
                    {
                        getRootMethod = m;
                        break;
                    }
                }
            }
            if (getRootMethod == null) return null;
            return getRootMethod.GetParameters().Length == 0
                ? getRootMethod.Invoke(syntaxTree, null)
                : getRootMethod.Invoke(syntaxTree, new object[] { default(CancellationToken) });
        }

        private static MethodInfo GetDescendantNodesMethod(object root)
        {
            if (root == null) return null;
            MethodInfo descMethod = null;
            foreach (var m in root.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name == "DescendantNodes" && m.GetParameters().Length == 2)
                {
                    descMethod = m;
                    break;
                }
            }
            if (descMethod == null)
            {
                foreach (var m in root.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name == "DescendantNodes" && m.GetParameters().Length == 0)
                    {
                        descMethod = m;
                        break;
                    }
                }
            }
            return descMethod;
        }

        private static readonly Regex s_UsingDirectiveRegex = new Regex(
            @"^\s*(?:global\s+)?using\s+(?:static\s+)?(?:[A-Za-z_][A-Za-z0-9_]*\s*=\s*)?[A-Za-z_][A-Za-z0-9_.]*(?:\s*<[^>]+>)?\s*;",
            RegexOptions.Compiled);

        public static bool ExtractUsingDirectives(string sourceCode, out List<string> usings, out string methodBody)
        {
            usings = new List<string>();
            methodBody = sourceCode ?? "";

            if (string.IsNullOrWhiteSpace(sourceCode))
            {
                return false;
            }

            if (IsSupported)
            {
                try
                {
                    object syntaxTree = ParseSyntaxTree(sourceCode);
                    if (syntaxTree != null)
                    {
                        object root = GetSyntaxTreeRoot(syntaxTree);
                        if (root != null)
                        {
                            MethodInfo descMethod = GetDescendantNodesMethod(root);
                            if (descMethod != null)
                            {
                                object[] descArgs = descMethod.GetParameters().Length == 2 ? new object[] { null, false } : null;
                                var nodes = (System.Collections.IEnumerable)descMethod.Invoke(root, descArgs);
                                if (nodes != null)
                                {
                                    var spansToRemove = new List<(int start, int length, string text)>();
                                    PropertyInfo fullSpanProp = null;

                                    foreach (var node in nodes)
                                    {
                                        if (node == null) continue;
                                        if (node.GetType().Name == "UsingDirectiveSyntax")
                                        {
                                            if (fullSpanProp == null) fullSpanProp = node.GetType().GetProperty("FullSpan");
                                            if (fullSpanProp != null)
                                            {
                                                object span = fullSpanProp.GetValue(node, null);
                                                int start = (int)span.GetType().GetProperty("Start").GetValue(span, null);
                                                int length = (int)span.GetType().GetProperty("Length").GetValue(span, null);
                                                string directiveText = node.ToString().Trim();
                                                if (!string.IsNullOrEmpty(directiveText))
                                                {
                                                    spansToRemove.Add((start, length, directiveText));
                                                }
                                            }
                                        }
                                    }

                                    if (spansToRemove.Count > 0)
                                    {
                                        spansToRemove.Sort((a, b) => a.start.CompareTo(b.start));
                                        var chars = sourceCode.ToCharArray();
                                        foreach (var (start, length, text) in spansToRemove)
                                        {
                                            usings.Add(text);
                                            int end = Math.Min(start + length, chars.Length);
                                            for (int i = start; i < end; i++)
                                            {
                                                if (chars[i] != '\r' && chars[i] != '\n')
                                                {
                                                    chars[i] = ' ';
                                                }
                                            }
                                        }
                                        methodBody = new string(chars);
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"UnityLeanMcp: Roslyn AST using extraction failed, falling back: {ex}");
                }
            }

            return ExtractUsingDirectivesFallback(sourceCode, out usings, out methodBody);
        }

        internal static bool ExtractUsingDirectivesFallback(string sourceCode, out List<string> usings, out string methodBody)
        {
            usings = new List<string>();
            methodBody = sourceCode ?? "";

            if (string.IsNullOrWhiteSpace(sourceCode)) return false;

            var lines = sourceCode.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            bool foundAny = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                while (true)
                {
                    var match = s_UsingDirectiveRegex.Match(line);
                    if (!match.Success)
                    {
                        break;
                    }

                    usings.Add(match.Value.Trim());
                    foundAny = true;

                    var charArray = line.ToCharArray();
                    int end = match.Index + match.Length;
                    for (int c = match.Index; c < end; c++)
                    {
                        charArray[c] = ' ';
                    }
                    line = new string(charArray);
                }
                lines[i] = line;
            }

            if (foundAny)
            {
                string separator = sourceCode.Contains("\r\n") ? "\r\n" : "\n";
                methodBody = string.Join(separator, lines);
                return true;
            }

            return false;
        }

        public static bool HasTopLevelValueReturn(string sourceCode)
        {
            if (string.IsNullOrWhiteSpace(sourceCode) || !IsSupported)
            {
                return false;
            }

            try
            {
                // If snippet has using directives, extract them first so probe method does not hit CS1529
                ExtractUsingDirectives(sourceCode, out _, out string cleanBody);

                // Wrap in a probe method so all C# language versions parse statements into a method body
                string probeSource = "class __Probe { async System.Threading.Tasks.Task<object> M() {\n" + cleanBody + "\n} }";

                object syntaxTree = ParseSyntaxTree(probeSource);
                if (syntaxTree == null) return false;

                object root = GetSyntaxTreeRoot(syntaxTree);
                if (root == null) return false;

                MethodInfo descMethod = GetDescendantNodesMethod(root);
                if (descMethod == null) return false;

                object[] descArgs = descMethod.GetParameters().Length == 2 ? new object[] { null, false } : null;
                var nodes = (System.Collections.IEnumerable)descMethod.Invoke(root, descArgs);
                if (nodes == null) return false;

                PropertyInfo parentProp = null;
                PropertyInfo exprProp = null;

                foreach (var node in nodes)
                {
                    if (node == null) continue;
                    Type nodeType = node.GetType();
                    if (nodeType.Name != "ReturnStatementSyntax") continue;

                    // Check ancestors to ensure not in nested lambda or local function
                    if (parentProp == null) parentProp = nodeType.GetProperty("Parent");
                    object cur = parentProp != null ? parentProp.GetValue(node, null) : null;
                    bool inNestedFunction = false;

                    while (cur != null)
                    {
                        string pName = cur.GetType().Name;
                        if (pName == "MethodDeclarationSyntax")
                        {
                            var idProp = cur.GetType().GetProperty("Identifier");
                            var idVal = idProp != null ? idProp.GetValue(cur, null) : null;
                            if (idVal != null && idVal.ToString() == "M")
                            {
                                break;
                            }
                            inNestedFunction = true;
                            break;
                        }
                        if (pName == "SimpleLambdaExpressionSyntax" ||
                            pName == "ParenthesizedLambdaExpressionSyntax" ||
                            pName == "AnonymousMethodExpressionSyntax" ||
                            pName == "LocalFunctionStatementSyntax")
                        {
                            inNestedFunction = true;
                            break;
                        }
                        cur = parentProp.GetValue(cur, null);
                    }

                    if (inNestedFunction) continue;

                    // Check Expression property of ReturnStatementSyntax
                    if (exprProp == null) exprProp = nodeType.GetProperty("Expression");
                    object expr = exprProp != null ? exprProp.GetValue(node, null) : null;
                    if (expr != null)
                    {
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"UnityLeanMcp: Failed to inspect snippet returns: {ex}");
                return false;
            }
        }
    }
}
