using System;

namespace UnityLeanMcp
{
    /// <summary>
    /// Serializes durable operation reads with cache updates. A readable file
    /// is authoritative; the cached record is retained only when the file
    /// cannot currently be read, so transient filesystem contention cannot
    /// turn a genuinely active operation into IDLE.
    /// </summary>
    internal sealed class OperationStateCache
    {
        private readonly object m_Lock = new object();
        private WorkerOperationStateSnapshot m_CachedState;

        internal OperationStateReadStatus Read(string path, out WorkerOperationStateSnapshot snapshot)
        {
            lock (m_Lock)
            {
                OperationStateReadStatus status = WorkerThreadSnapshots.ReadOperationState(path, out var durableState);
                switch (status)
                {
                    case OperationStateReadStatus.Valid:
                        m_CachedState = durableState;
                        snapshot = durableState;
                        break;
                    case OperationStateReadStatus.Missing:
                    case OperationStateReadStatus.Invalid:
                        m_CachedState = null;
                        snapshot = null;
                        break;
                    case OperationStateReadStatus.Unavailable:
                        snapshot = m_CachedState;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                return status;
            }
        }

        internal void Set(WorkerOperationStateSnapshot state)
        {
            lock (m_Lock)
            {
                m_CachedState = state;
            }
        }

        internal void Clear()
        {
            Set(null);
        }

        internal WorkerOperationStateSnapshot GetCached()
        {
            lock (m_Lock)
            {
                return m_CachedState;
            }
        }
    }
}
