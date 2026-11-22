using System;
using System.Collections.Concurrent;
using UnityEditor;
using UnityEngine;

namespace UnityLeanMcp
{
    internal static class UnityLeanMcpDispatcher
    {
        private static readonly ConcurrentQueue<Action> s_Queue = new ConcurrentQueue<Action>();
        private static volatile bool s_Initialized;
        private static readonly object s_InitLock = new object();

        public static void EnsureInitialized()
        {
            if (s_Initialized) return;
            lock (s_InitLock)
            {
                if (s_Initialized) return;
                EditorApplication.update -= Update;
                EditorApplication.update += Update;
                s_Initialized = true;
            }
        }

        public static void Enqueue(Action action)
        {
            s_Queue.Enqueue(action);
            try
            {
                EditorApplication.QueuePlayerLoopUpdate();
            }
            catch { }
        }

        public static void Clear()
        {
            while (s_Queue.TryDequeue(out _))
            {
            }
        }

        public static void Update()
        {
            bool any = false;
            while (s_Queue.TryDequeue(out var action))
            {
                any = true;
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
            if (any)
            {
                try
                {
                    EditorApplication.QueuePlayerLoopUpdate();
                }
                catch { }
            }
        }
    }
}
