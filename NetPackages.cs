using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using UnityEngine.Scripting;

namespace WebMap
{
    // The mod's only network package, in both directions. One type so NetCompat has a single slot to hide from
    // clients without the mod.
    //   Client -> server, Subscribe: this player's web map is open (and showing workstations), or not any more.
    //   Server -> client, Workstations: workstations that changed (JSON objects, see WorkstationTracker.Snapshot)
    //   and ids removed. Full means this is everything the player can see; the client drops any not listed.
    [Preserve]
    [SuppressMessage("ReSharper", "UnusedType.Global")]
    public class NetPackageWebMap : NetPackage
    {
        private const byte KindSubscribe = 0;
        private const byte KindWorkstations = 1;

        private byte kind;
        private bool flag; // Subscribe: on. Workstations: full.
        private readonly List<string> set = new List<string>();
        private readonly List<string> del = new List<string>();

        public override bool Compress => true;

        public NetPackageWebMap SetupSubscribe(bool _on)
        {
            kind = KindSubscribe;
            flag = _on;
            set.Clear();
            del.Clear();
            return this;
        }

        public NetPackageWebMap SetupWorkstations(bool _full, List<string> _set, List<string> _del)
        {
            kind = KindWorkstations;
            flag = _full;
            set.Clear();
            set.AddRange(_set);
            del.Clear();
            del.AddRange(_del);
            return this;
        }

        public override void read(PooledBinaryReader _reader)
        {
            kind = _reader.ReadByte();
            flag = _reader.ReadBoolean();
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
            _writer.Write(kind);
            _writer.Write(flag);
            _writer.Write(set.Count);
            for (int i = 0; i < set.Count; i++) _writer.Write(set[i]);
            _writer.Write(del.Count);
            for (int i = 0; i < del.Count; i++) _writer.Write(del[i]);
        }

        public override void ProcessPackage(World _world, GameManager _callbacks)
        {
            if (_world == null) return;
            bool isServer = SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer;
            if (kind == KindSubscribe)
            {
                if (isServer && Sender != null) WorkstationTracker.SetSubscribed(Sender.entityId, flag);
            }
            else if (kind == KindWorkstations)
            {
                if (!isServer) WorkstationClient.Receive(flag, set, del);
            }
        }
    }
}
