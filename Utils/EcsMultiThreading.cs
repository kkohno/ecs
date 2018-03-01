// ----------------------------------------------------------------------------
// The MIT License
// Simple Entity Component System framework https://github.com/Leopotam/ecs
// Copyright (c) 2017-2018 Leopotam <leopotam@gmail.com>
// ----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
#if !UNITY_WEBGL
using System.Threading;
#endif

namespace LeopotamGroup.Ecs {
#if UNITY_WEBGL
#warning Multithreading not supported in WebGL
    /// <summary>
    /// Stub for base system of multithreading processing. Will work like IEcsRunSystem system.
    /// </summary>
    public abstract class EcsMultiThreadSystem : IEcsPreInitSystem, IEcsRunSystem {
        EcsMultiThreadJob _localJob;

        EcsWorld _world;

        EcsFilter _filter;

        Action<EcsMultiThreadJob> _worker;

        public void ForceSync () { }

        void IEcsPreInitSystem.PreInitialize () {
            _world = GetWorld ();
            _filter = GetFilter ();
            _worker = GetWorker ();
#if DEBUG
            if (_world == null) {
                throw new Exception ("Invalid EcsWorld");
            }
            if (_filter == null) {
                throw new Exception ("Invalid EcsFilter");
            }
            if (GetMinJobSize () < 1) {
                throw new Exception ("Invalid JobSize");
            }
            if (GetThreadsCount () < 1) {
                throw new Exception ("Invalid ThreadsCount");
            }
#endif
            _localJob = new EcsMultiThreadJob ();
            _localJob.World = _world;
            _localJob.From = 0;
            _localJob.Entities = _filter.Entities;
        }

        void IEcsPreInitSystem.PreDestroy () {
            _world = null;
            _filter = null;
            _worker = null;
        }

        void IEcsRunSystem.Run () {
            _localJob.To = _filter.Entities.Count;
            _worker (_localJob);
        }

        protected abstract EcsWorld GetWorld ();

        protected abstract EcsFilter GetFilter ();

        protected abstract Action<EcsMultiThreadJob> GetWorker ();

        protected abstract int GetMinJobSize ();

        protected abstract int GetThreadsCount ();
    }
#else
    /// <summary>
    /// Base system for multithreading processing.
    /// </summary>
    public abstract class EcsMultiThreadSystem : IEcsPreInitSystem, IEcsRunSystem {
        WorkerDesc[] _descs;

        ManualResetEvent[] _syncs;

        EcsMultiThreadJob _localJob;

        EcsWorld _world;

        EcsFilter _filter;

        Action<EcsMultiThreadJob> _worker;

        int _minJobSize;

        int _threadsCount;

        bool _forceSyncState;

        /// <summary>
        /// Force synchronized threads to main thread (lock main thread and await results from threads).
        /// </summary>
        public void ForceSync () {
            WaitHandle.WaitAll (_syncs);
        }

        void IEcsPreInitSystem.PreDestroy () {
            for (var i = 0; i < _descs.Length; i++) {
                var desc = _descs[i];
                _descs[i] = null;
                desc.Thread.Interrupt ();
                desc.Thread.Join (10);
                _syncs[i].Close ();
                _syncs[i] = null;
            }
            _world = null;
            _filter = null;
            _worker = null;
        }

        void IEcsPreInitSystem.PreInitialize () {
            _world = GetWorld ();
            _filter = GetFilter ();
            _worker = GetWorker ();
            _minJobSize = GetMinJobSize ();
            _threadsCount = GetThreadsCount ();
            _forceSyncState = GetForceSyncState ();
#if DEBUG
            if (_world == null) {
                throw new Exception ("Invalid EcsWorld");
            }
            if (_filter == null) {
                throw new Exception ("Invalid EcsFilter");
            }
            if (_minJobSize < 1) {
                throw new Exception ("Invalid JobSize");
            }
            if (_threadsCount < 1) {
                throw new Exception ("Invalid ThreadsCount");
            }
            var hash = this.GetHashCode ();
#endif
            _descs = new WorkerDesc[_threadsCount];
            _syncs = new ManualResetEvent[_threadsCount];
            EcsMultiThreadJob job;
            for (var i = 0; i < _descs.Length; i++) {
                job = new EcsMultiThreadJob ();
                job.World = _world;
                job.Entities = _filter.Entities;
                var desc = new WorkerDesc ();
                desc.Job = job;
                desc.Thread = new Thread (ThreadProc);
                desc.Thread.IsBackground = true;
#if DEBUG
                desc.Thread.Name = string.Format ("ECS-{0:X}-{1}", hash, i);
#endif
                desc.HasWork = new ManualResetEvent (false);
                desc.WorkDone = new ManualResetEvent (true);
                desc.Worker = _worker;
                _descs[i] = desc;
                _syncs[i] = desc.WorkDone;
                desc.Thread.Start (desc);
            }
            _localJob = new EcsMultiThreadJob ();
            _localJob.World = _world;
            _localJob.Entities = _filter.Entities;
        }

        void IEcsRunSystem.Run () {
            var count = _filter.Entities.Count;
            var processed = 0;
            var jobSize = count / (_threadsCount + 1);
            int workersCount;
            if (jobSize > _minJobSize) {
                workersCount = _threadsCount + 1;
            } else {
                workersCount = count / _minJobSize;
                jobSize = _minJobSize;
            }
            for (var i = 0; i < workersCount - 1; i++) {
                var desc = _descs[i];
                desc.Job.From = processed;
                processed += jobSize;
                desc.Job.To = processed;
                desc.WorkDone.Reset ();
                desc.HasWork.Set ();
            }
            _localJob.From = processed;
            _localJob.To = count;
            _worker (_localJob);
            if (_forceSyncState) {
                ForceSync ();
            }
        }

        void ThreadProc (object rawDesc) {
            var desc = (WorkerDesc) rawDesc;
            try {
                while (Thread.CurrentThread.IsAlive) {
                    desc.HasWork.WaitOne ();
                    desc.HasWork.Reset ();
                    desc.Worker (desc.Job);
                    desc.WorkDone.Set ();
                }
            } catch { }
        }

        /// <summary>
        /// EcsWorld instance to use in custom worker.
        /// </summary>
        protected abstract EcsWorld GetWorld ();

        /// <summary>
        /// Source filter for processing entities from it.
        /// </summary>
        protected abstract EcsFilter GetFilter ();

        /// <summary>
        /// Custom processor of received entities.
        /// </summary>
        protected abstract Action<EcsMultiThreadJob> GetWorker ();

        /// <summary>
        /// Minimal amount of entities to process by one worker.
        /// </summary>
        protected abstract int GetMinJobSize ();

        /// <summary>
        /// How many threads should be used by this system.
        /// </summary>
        protected abstract int GetThreadsCount ();

        /// <summary>
        /// Should threads be force synchronized to main thread (lock main thread and await results from threads).
        /// Use with care - ForceSync() method should be called in current update frame!
        /// </summary>
        protected virtual bool GetForceSyncState () {
            return true;
        }

        sealed class WorkerDesc {
            public Thread Thread;
            public ManualResetEvent HasWork;
            public ManualResetEvent WorkDone;
            public Action<EcsMultiThreadJob> Worker;
            public EcsMultiThreadJob Job;
        }

    }
#endif
    /// <summary>
    /// Job info for multithreading processing.
    /// </summary>
    public struct EcsMultiThreadJob {
        /// <summary>
        /// EcsWorld instance.
        /// </summary>
        public IEcsReadOnlyWorld World;

        /// <summary>
        /// Entities list to processing.
        /// </summary>
        public List<int> Entities;

        /// <summary>
        /// Index of first entity in list to processing.
        /// </summary>
        public int From;

        /// <summary>
        /// Index of entity after last item to processing (should be excluded from processing).
        /// </summary>
        public int To;
    }
}