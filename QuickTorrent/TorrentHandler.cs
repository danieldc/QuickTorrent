﻿using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using MonoTorrent.Client.Encryption;
using MonoTorrent.Common;
using MonoTorrent.Dht;
using MonoTorrent.Dht.Listeners;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;

namespace QuickTorrent
{
    public struct PiecemapEventArgs
    {
        /// <summary>
        /// The entire piece map. Enabled pieces are already downloaded
        /// </summary>
        public readonly bool[] Piecemap;
        /// <summary>
        /// The last piece index updated
        /// </summary>
        public readonly int LastPiece;
        public readonly bool Complete;

        public PiecemapEventArgs(bool[] Piecemap, int LastPiece)
        {
            this.Piecemap = Piecemap;
            this.LastPiece = LastPiece;
            Complete = Piecemap != null && Piecemap.All(m => m);
        }
    }

    public class TorrentHandler : IDisposable
    {
        public delegate void PiecemapUpdateHandler(object Sender, PiecemapEventArgs Args);

        public event PiecemapUpdateHandler PiecemapUpdate = delegate { };

        public const string DOWNLOAD_DIR = @"%USERPROFILE%\Downloads\QuickTorrent";
        public const string TORRENT_DIR = @"%APPDATA%\QuickTorrent\TorrentCache";
        public const string DHTFILE = @"%APPDATA%\QuickTorrent\dht.bin";

        private static DhtListener DL;
        private static DhtEngine DE;
        private static ClientEngine CE;
        private static EngineSettings ES;

        private TorrentManager TM;
        private TorrentSettings TS;
        private bool[] PieceMap;

        public bool HasAllPieces
        {
            get
            {
                return PieceMap.All(m => m);
            }
        }
        public bool IsComplete
        {
            get
            {
                return TM.Complete || TM.State == TorrentState.Seeding;
            }
        }
        public bool IsPaused
        {
            get
            {
                return TM.State == TorrentState.Paused;
            }
        }
        public bool IsDownloading
        {
            get
            {
                return TM.State == TorrentState.Downloading ||
                    TM.State == TorrentState.Hashing ||
                    TM.State == TorrentState.Metadata;
            }
        }
        public double Progress
        {
            get
            {
                return TM.Progress;
            }
        }
        public TorrentState State
        {
            get
            {
                return TM.State;
            }
        }
        public int ConnectedPeers
        {
            get
            {
                return TM.OpenConnections;
            }
        }
        public int Seeds
        {
            get
            {
                return TM.Peers.Seeds;
            }
        }
        public int Leechs
        {
            get
            {
                return TM.Peers.Leechs;
            }
        }
        public int Files
        {
            get
            {
                return TM.Torrent != null ? TM.Torrent.Files.Length : 0;
            }
        }
        public long TotalSize
        {
            get
            {
                return TM.HasMetadata ? TM.Torrent.Size : 0;
            }
        }
        public string TorrentName
        {
            get
            {
                return TM.Torrent?.Name == null ? TM.InfoHash.ToHex() : TM.Torrent.Name;
            }
        }
        public string InfoHash
        {
            get
            {
                return TM.InfoHash.ToHex();
            }
        }
        public bool[] Map
        {
            get
            {
                return PieceMap == null ? null : (bool[])PieceMap.Clone();
            }
        }

        public TorrentHandler(MagnetLink ML, string DownloadDir = DOWNLOAD_DIR)
        {
            InitBase(DownloadDir);
            TM = new TorrentManager(ML, Environment.ExpandEnvironmentVariables(DownloadDir), TS, Environment.ExpandEnvironmentVariables(TORRENT_DIR));
            Assign();

        }

        public TorrentHandler(Torrent T, string DownloadDir = DOWNLOAD_DIR)
        {
            InitBase(DownloadDir);
            PieceMap = new bool[T.Pieces.Count];
            TM = new TorrentManager(T, Environment.ExpandEnvironmentVariables(DownloadDir), TS);
            Assign();

        }

        public TorrentHandler(InfoHash H, string DownloadDir = DOWNLOAD_DIR, bool ForceHash = false)
        {
            InitBase(DownloadDir);
            var CacheFile = Environment.ExpandEnvironmentVariables(TORRENT_DIR + $"\\{H.ToHex()}.torrent");
            //Use cached torrent file if available
            if (!ForceHash && File.Exists(CacheFile))
            {
                Torrent T = Torrent.Load(File.ReadAllBytes(CacheFile));
                TM = new TorrentManager(T, Environment.ExpandEnvironmentVariables(DownloadDir), TS);
            }
            else
            {
                TM = new TorrentManager(H, Environment.ExpandEnvironmentVariables(DownloadDir), TS, Environment.ExpandEnvironmentVariables(TORRENT_DIR), new List<RawTrackerTier>());
            }
            Assign();
        }

        public static void StartAll()
        {
            CE.StartAll();
        }

        public static void StopAll()
        {
            CE.StopAll();
        }

        public void Start()
        {
            TM.Start();
        }

        public void Stop()
        {
            TM.Stop();
        }

        private void InitBase(string DownloadDir)
        {
            if (!Directory.Exists(Environment.ExpandEnvironmentVariables(DownloadDir)))
            {
                Directory.CreateDirectory(Environment.ExpandEnvironmentVariables(DownloadDir));
            }
            if (!Directory.Exists(Environment.ExpandEnvironmentVariables(TORRENT_DIR)))
            {
                Directory.CreateDirectory(Environment.ExpandEnvironmentVariables(TORRENT_DIR));
            }

            //Initialize DHT engine for the first time
            lock ("DHT_Create")
            {
                if (DE == null)
                {
                    DL = new DhtListener(new IPEndPoint(IPAddress.Any, 54321));
                    DE = new DhtEngine(DL);
                    if (File.Exists(Environment.ExpandEnvironmentVariables(DHTFILE)))
                    {
                        DE.Start(File.ReadAllBytes(Environment.ExpandEnvironmentVariables(DHTFILE)));
                    }
                    else
                    {
                        DE.Start();
                    }
                    DL.Start();
                }
            }
            lock ("CE_Create")
            {
                if (ES == null)
                {
                    ES = new EngineSettings(Environment.ExpandEnvironmentVariables(DownloadDir), 54321, 500, 250, 0, 0, EncryptionTypes.All);
                }
                if (CE == null)
                {
                    CE = new ClientEngine(ES);
                    CE.RegisterDht(DE);
                }
            }
            TS = new TorrentSettings(10, 200, 0, 0);
        }

        public void SetComplete()
        {
            PieceMap = PieceMap.Select(m => true).ToArray();
        }

        public void Hash()
        {
            if (TM.State != TorrentState.Hashing)
            {
                TM.HashCheck(true);
            }
        }

        public void SaveRecovery()
        {
            if (TM.HasMetadata)
            {
                var RecoveryFile = Environment.ExpandEnvironmentVariables(TORRENT_DIR + $"\\{TM.InfoHash.ToHex()}.rec");
                var FR = TM.SaveFastResume();
                if (File.Exists(RecoveryFile))
                {
                    File.Delete(RecoveryFile);
                }
                using (var FS = File.Create(RecoveryFile))
                {
                    FR.Encode(FS);
                }
            }
        }

        private void Assign()
        {
            TM.PieceHashed += (a, b) =>
            {
                //if the PieceMap is null, this was a magnet link torrent but we know the map now
                if (PieceMap == null)
                {
                    PieceMap = new bool[TM.Torrent.Pieces.Count];
                }
                lock (PieceMap)
                {
                    PieceMap[b.PieceIndex] = b.HashPassed;
                }
                PiecemapUpdate(this, new PiecemapEventArgs(Map, b.PieceIndex));
            };
            TM.PieceManager.BlockReceived += (a, b) =>
            {
                PieceMap[b.Piece.Index] = true;
                PiecemapUpdate(this, new PiecemapEventArgs(Map, b.Piece.Index));
            };

            var RecoveryFile = Environment.ExpandEnvironmentVariables(TORRENT_DIR + $"\\{TM.InfoHash.ToHex()}.rec");
            if (File.Exists(RecoveryFile))
            {
                TM.LoadFastResume(new FastResume((BEncodedDictionary)BEncodedDictionary.Decode(File.ReadAllBytes(RecoveryFile))));
            }

            CE.Register(TM);
        }

        public void Dispose()
        {
            if (TM != null && CE != null)
            {
                CE.Unregister(TM);
                Stop();
                TM.Dispose();
                TM = null;
            }
        }

        public static void SaveDhtNodes()
        {
            File.WriteAllBytes(Environment.ExpandEnvironmentVariables(DHTFILE), DE.SaveNodes());
        }
    }
}
