using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using UnityEngine.Scripting;

namespace WebMap
{
    // Client -> server: this player's web map is open (and showing workstations), or not any more
    [Preserve]
    [SuppressMessage("ReSharper", "UnusedType.Global")]
    public class NetPackageWebMapSubscribe : NetPackage
    {
        private bool on;

        public override NetPackageDirection PackageDirection => NetPackageDirection.ToServer;

        public NetPackageWebMapSubscribe Setup(bool _on)
        {
            on = _on;
            return this;
        }

        public override void read(PooledBinaryReader _reader)
        {
            on = _reader.ReadBoolean();
        }

        public override void write(PooledBinaryWriter _writer)
        {
            base.write(_writer);
            _writer.Write(on);
        }

        public override void ProcessPackage(World _world, GameManager _callbacks)
        {
            if (_world == null || Sender == null) return;
            WorkstationTracker.SetSubscribed(Sender.entityId, on);
        }
    }

    // Server -> client: workstations that changed (JSON objects, see WorkstationTracker.Snapshot) and ids removed.
    // Full means this is everything the player can see; the client drops any it has that aren't listed.
    [Preserve]
    [SuppressMessage("ReSharper", "UnusedType.Global")]
    public class NetPackageWebMapWorkstations : NetPackage
    {
        private bool full;
        private readonly List<string> set = new List<string>();
        private readonly List<string> del = new List<string>();

        public override NetPackageDirection PackageDirection => NetPackageDirection.ToClient;
        public override bool Compress => true;

        public NetPackageWebMapWorkstations Setup(bool _full, List<string> _set, List<string> _del)
        {
            full = _full;
            set.Clear();
            set.AddRange(_set);
            del.Clear();
            del.AddRange(_del);
            return this;
        }

        public override void read(PooledBinaryReader _reader)
        {
            full = _reader.ReadBoolean();
            set.Clear();
            int n = _reader.ReadInt32();
            for (int i = 0; i < n; i++) set.Add(_reader.ReadString());
            del.Clear();
            n = _reader.ReadInt32();
            for (int i = 0; i < n; i++) del.Add(_reader.ReadString());
        }

        public override void write(PooledBinaryWriter _writer)
        {
            base.write(_writer);
            _writer.Write(full);
            _writer.Write(set.Count);
            for (int i = 0; i < set.Count; i++) _writer.Write(set[i]);
            _writer.Write(del.Count);
            for (int i = 0; i < del.Count; i++) _writer.Write(del[i]);
        }

        public override void ProcessPackage(World _world, GameManager _callbacks)
        {
            if (_world == null) return;
            WorkstationClient.Receive(full, set, del);
        }
    }
}
