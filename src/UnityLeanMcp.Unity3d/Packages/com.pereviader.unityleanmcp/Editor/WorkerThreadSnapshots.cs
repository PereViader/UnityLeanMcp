using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace UnityLeanMcp
{
    internal enum OperationStateReadStatus
    {
        Valid,
        Missing,
        Invalid,
        Unavailable
    }

    /// <summary>
    /// Immutable data read by socket worker threads. This type deliberately has
    /// no Unity dependencies so a worker can inspect durable state while the
    /// main thread is executing synchronously or reloading its domain.
    /// </summary>
    internal sealed class WorkerOperationStateSnapshot
    {
        public WorkerOperationStateSnapshot(string operationId, string kind, string status, string editorSessionId, string startedUtc, string updatedUtc)
        {
            OperationId = operationId;
            Kind = kind;
            Status = status;
            EditorSessionId = editorSessionId;
            StartedUtc = startedUtc;
            UpdatedUtc = updatedUtc;
        }

        public string OperationId { get; }
        public string Kind { get; }
        public string Status { get; }
        public string EditorSessionId { get; }
        public string StartedUtc { get; }
        public string UpdatedUtc { get; }
    }

    internal sealed class WorkerTestRunStateSnapshot
    {
        public WorkerTestRunStateSnapshot(string runId)
        {
            RunId = runId;
        }

        public string RunId { get; }
    }

    internal sealed class WorkerOperationResultSnapshot
    {
        public WorkerOperationResultSnapshot(string operationId, bool success, bool interrupted, string message, string payload, string resultState, int failCount, int passCount, int skipCount)
        {
            OperationId = operationId;
            Success = success;
            Interrupted = interrupted;
            Message = message;
            Payload = payload;
            ResultState = resultState;
            FailCount = failCount;
            PassCount = passCount;
            SkipCount = skipCount;
        }

        public string OperationId { get; }
        public bool Success { get; }
        public bool Interrupted { get; }
        public string Message { get; }
        public string Payload { get; }
        public string ResultState { get; }
        public int FailCount { get; }
        public int PassCount { get; }
        public int SkipCount { get; }
    }

    internal sealed class WorkerRefreshResultSnapshot
    {
        public WorkerRefreshResultSnapshot(string operationId, bool success, bool interrupted, string message)
        {
            OperationId = operationId;
            Success = success;
            Interrupted = interrupted;
            Message = message;
        }

        public string OperationId { get; }
        public bool Success { get; }
        public bool Interrupted { get; }
        public string Message { get; }
    }

    internal static class WorkerThreadSnapshots
    {
        internal static OperationStateReadStatus ReadOperationState(string path, out WorkerOperationStateSnapshot snapshot)
        {
            snapshot = null;
            FileReadStatus fileStatus = TryReadFileWithStatus(path, out string json);
            if (fileStatus == FileReadStatus.Missing)
            {
                return OperationStateReadStatus.Missing;
            }

            if (fileStatus == FileReadStatus.Unavailable)
            {
                return OperationStateReadStatus.Unavailable;
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                return OperationStateReadStatus.Invalid;
            }

            Dictionary<string, object> values;
            try
            {
                values = new JsonObjectReader(json).ReadObject();
            }
            catch (Exception)
            {
                return OperationStateReadStatus.Invalid;
            }

            string operationId = GetString(values, "operationId");
            string kind = GetString(values, "kind");
            if (!IsValidToken(operationId) || !IsValidToken(kind))
            {
                return OperationStateReadStatus.Invalid;
            }

            snapshot = new WorkerOperationStateSnapshot(
                operationId,
                kind,
                GetString(values, "status"),
                GetString(values, "editorSessionId"),
                GetString(values, "startedUtc"),
                GetString(values, "updatedUtc"));
            return OperationStateReadStatus.Valid;
        }

        internal static bool TryReadOperationState(string path, out WorkerOperationStateSnapshot snapshot)
        {
            return ReadOperationState(path, out snapshot) == OperationStateReadStatus.Valid;
        }

        internal static bool TryReadTestRunState(string path, out WorkerTestRunStateSnapshot snapshot)
        {
            snapshot = null;
            if (!TryReadObject(path, out var values))
            {
                return false;
            }

            string runId = GetString(values, "runId");
            if (string.IsNullOrEmpty(runId))
            {
                return false;
            }

            snapshot = new WorkerTestRunStateSnapshot(runId);
            return true;
        }

        internal static bool TryReadOperationResult(string path, out WorkerOperationResultSnapshot snapshot)
        {
            snapshot = null;
            if (!TryReadObject(path, out var values))
            {
                return false;
            }

            string operationId = GetString(values, "operationId");
            if (string.IsNullOrEmpty(operationId))
            {
                operationId = GetString(values, "runId");
            }

            if (string.IsNullOrEmpty(operationId))
            {
                return false;
            }

            snapshot = new WorkerOperationResultSnapshot(
                operationId,
                GetBoolean(values, "success"),
                GetBoolean(values, "interrupted") || string.Equals(GetString(values, "resultState"), "Interrupted", StringComparison.Ordinal),
                GetString(values, "message"),
                GetString(values, "payload"),
                GetString(values, "resultState"),
                GetInt32(values, "failCount"),
                GetInt32(values, "passCount"),
                GetInt32(values, "skipCount"));
            return true;
        }

        internal static bool TryReadRefreshResult(string path, out WorkerRefreshResultSnapshot snapshot)
        {
            snapshot = null;
            if (!TryReadObject(path, out var values))
            {
                return false;
            }

            string operationId = GetString(values, "operationId");
            if (string.IsNullOrEmpty(operationId))
            {
                return false;
            }

            snapshot = new WorkerRefreshResultSnapshot(
                operationId,
                GetBoolean(values, "success"),
                GetBoolean(values, "interrupted"),
                GetString(values, "message"));
            return true;
        }

        internal static string ReadFileWithRetry(string path, int maxRetries = 3, int delayMs = 10)
        {
            FileReadStatus status = TryReadFileWithStatus(path, out string content, maxRetries, delayMs);
            return status == FileReadStatus.Success ? content : null;
        }

        internal enum FileReadStatus
        {
            Success,
            Missing,
            Unavailable
        }

        internal static FileReadStatus TryReadFileWithStatus(
            string path,
            out string content,
            int maxRetries = 3,
            int delayMs = 10)
        {
            content = null;
            if (string.IsNullOrEmpty(path) || maxRetries <= 0)
            {
                return FileReadStatus.Missing;
            }

            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        content = reader.ReadToEnd();
                        return FileReadStatus.Success;
                    }
                }
                catch (FileNotFoundException)
                {
                    return FileReadStatus.Missing;
                }
                catch (DirectoryNotFoundException)
                {
                    return FileReadStatus.Missing;
                }
                catch (IOException) when (i < maxRetries - 1)
                {
                    Thread.Sleep(delayMs);
                }
                catch (UnauthorizedAccessException) when (i < maxRetries - 1)
                {
                    Thread.Sleep(delayMs);
                }
                catch (IOException)
                {
                    return FileReadStatus.Unavailable;
                }
                catch (UnauthorizedAccessException)
                {
                    return FileReadStatus.Unavailable;
                }
            }
            return FileReadStatus.Unavailable;
        }

        internal static bool TryReadTestCancellationRequest(string path, string runId)
        {
            if (!IsValidToken(runId) || string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return false;
            }

            string requestedRunId = ReadFileWithRetry(path);
            return string.Equals(requestedRunId?.Trim(), runId, StringComparison.Ordinal);
        }

        internal static bool TryWriteTestCancellationRequest(string path, string runId)
        {
            if (!IsValidToken(runId) || string.IsNullOrEmpty(path))
            {
                return false;
            }

            try
            {
                string directory = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(directory))
                {
                    return false;
                }

                Directory.CreateDirectory(directory);
                if (TryReadTestCancellationRequest(path, runId))
                {
                    return true;
                }

                if (File.Exists(path))
                {
                    // A marker for another run can only be stale here: the
                    // operation journal serializes test runs. Remove it so a
                    // newly accepted request is not lost after a crash.
                    File.Delete(path);
                }

                string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(tempPath, runId, new UTF8Encoding(false));
                    try
                    {
                        // The destination is intentionally created only by the
                        // move. Repeated requests race safely: the losing
                        // writer verifies that the winner recorded this run.
                        File.Move(tempPath, path);
                        return true;
                    }
                    catch (IOException)
                    {
                        return TryReadTestCancellationRequest(path, runId);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return TryReadTestCancellationRequest(path, runId);
                    }
                }
                finally
                {
                    if (File.Exists(tempPath))
                    {
                        try { File.Delete(tempPath); } catch { }
                    }
                }
            }
            catch (IOException)
            {
                return TryReadTestCancellationRequest(path, runId);
            }
            catch (UnauthorizedAccessException)
            {
                return TryReadTestCancellationRequest(path, runId);
            }
        }

        private static bool TryReadObject(string path, out Dictionary<string, object> values)
        {
            values = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return false;
            }

            string json = ReadFileWithRetry(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            try
            {
                values = new JsonObjectReader(json).ReadObject();
                return values != null;
            }
            catch (FormatException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static string GetString(Dictionary<string, object> values, string key)
        {
            return values.TryGetValue(key, out var value) ? value as string : null;
        }

        private static bool GetBoolean(Dictionary<string, object> values, string key)
        {
            return values.TryGetValue(key, out var value) && value is bool boolean && boolean;
        }

        private static int GetInt32(Dictionary<string, object> values, string key)
        {
            if (!values.TryGetValue(key, out var value))
            {
                return 0;
            }

            if (value is long longValue)
            {
                return longValue > int.MaxValue ? int.MaxValue : (longValue < int.MinValue ? int.MinValue : (int)longValue);
            }

            if (value is double doubleValue)
            {
                return doubleValue > int.MaxValue ? int.MaxValue : (doubleValue < int.MinValue ? int.MinValue : (int)doubleValue);
            }

            return 0;
        }

        private static bool IsValidToken(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 128)
            {
                return false;
            }

            foreach (char c in value)
            {
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.'))
                {
                    return false;
                }
            }

            return true;
        }

        private sealed class JsonObjectReader
        {
            private readonly string _json;
            private int _index;

            public JsonObjectReader(string json)
            {
                _json = json;
            }

            public Dictionary<string, object> ReadObject()
            {
                SkipWhitespace();
                Expect('{');
                var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                SkipWhitespace();
                if (TryConsume('}'))
                {
                    return values;
                }

                while (true)
                {
                    SkipWhitespace();
                    string key = ReadString();
                    SkipWhitespace();
                    Expect(':');
                    values[key] = ReadValue();
                    SkipWhitespace();
                    if (TryConsume('}'))
                    {
                        SkipWhitespace();
                        if (_index != _json.Length)
                        {
                            throw new FormatException("Unexpected JSON content.");
                        }
                        return values;
                    }
                    Expect(',');
                }
            }

            private object ReadValue()
            {
                SkipWhitespace();
                if (_index >= _json.Length)
                {
                    throw new FormatException("Unexpected end of JSON.");
                }

                char c = _json[_index];
                if (c == '"') return ReadString();
                if (c == '{') return ReadNestedObject();
                if (c == '[') return ReadArray();
                if (StartsWith("true")) { _index += 4; return true; }
                if (StartsWith("false")) { _index += 5; return false; }
                if (StartsWith("null")) { _index += 4; return null; }
                return ReadNumber();
            }

            private Dictionary<string, object> ReadNestedObject()
            {
                Expect('{');
                var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                SkipWhitespace();
                if (TryConsume('}')) return values;
                while (true)
                {
                    SkipWhitespace();
                    string key = ReadString();
                    SkipWhitespace();
                    Expect(':');
                    values[key] = ReadValue();
                    SkipWhitespace();
                    if (TryConsume('}')) return values;
                    Expect(',');
                }
            }

            private List<object> ReadArray()
            {
                Expect('[');
                var values = new List<object>();
                SkipWhitespace();
                if (TryConsume(']')) return values;
                while (true)
                {
                    values.Add(ReadValue());
                    SkipWhitespace();
                    if (TryConsume(']')) return values;
                    Expect(',');
                }
            }

            private string ReadString()
            {
                Expect('"');
                var builder = new StringBuilder();
                while (_index < _json.Length)
                {
                    char c = _json[_index++];
                    if (c == '"') return builder.ToString();
                    if (c != '\\')
                    {
                        builder.Append(c);
                        continue;
                    }

                    if (_index >= _json.Length) throw new FormatException("Invalid JSON escape.");
                    char escape = _json[_index++];
                    switch (escape)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u': builder.Append(ReadUnicodeEscape()); break;
                        default: throw new FormatException("Invalid JSON escape.");
                    }
                }
                throw new FormatException("Unterminated JSON string.");
            }

            private char ReadUnicodeEscape()
            {
                if (_index + 4 > _json.Length) throw new FormatException("Invalid JSON unicode escape.");
                string hex = _json.Substring(_index, 4);
                _index += 4;
                if (!ushort.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort value))
                {
                    throw new FormatException("Invalid JSON unicode escape.");
                }
                return (char)value;
            }

            private double ReadNumber()
            {
                int start = _index;
                while (_index < _json.Length && "0123456789+-.eE".IndexOf(_json[_index]) >= 0)
                {
                    _index++;
                }

                string value = _json.Substring(start, _index - start);
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                {
                    throw new FormatException("Invalid JSON number.");
                }
                return number;
            }

            private bool StartsWith(string value)
            {
                return _index + value.Length <= _json.Length && string.CompareOrdinal(_json, _index, value, 0, value.Length) == 0;
            }

            private void SkipWhitespace()
            {
                while (_index < _json.Length && char.IsWhiteSpace(_json[_index])) _index++;
            }

            private void Expect(char expected)
            {
                if (_index >= _json.Length || _json[_index] != expected)
                {
                    throw new FormatException("Invalid JSON structure.");
                }
                _index++;
            }

            private bool TryConsume(char value)
            {
                if (_index < _json.Length && _json[_index] == value)
                {
                    _index++;
                    return true;
                }
                return false;
            }
        }
    }
}
