﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Runtime.InteropServices;
using GoAhead.Code;
using GoAhead.Code.XDL;
using GoAhead.Commands.NetlistContainerGeneration;
using GoAhead.Commands.Selection;
using GoAhead.FPGA;
using GoAhead.Objects;
using GoAhead.Code.Dijkstra;
using Dijkstra.NET.Graph;
using Dijkstra.NET.ShortestPath;

namespace GoAhead.Commands
{
    class DijkstraRouteNet : NetlistContainerCommand
    {
        protected override void DoCommandAction()
        {
            // Not fully sure about what this whole function body does.
            FPGATypes.AssertBackendType(FPGATypes.BackendType.ISE);

            // what to route
            NetlistContainer netlist = GetNetlistContainer();
            XDLNet netToRoute = (XDLNet)netlist.GetNet(NetName);

            int outpinCount = netToRoute.NetPins.Count(np => np is NetOutpin);
            if (outpinCount != 1)
            {
                throw new ArgumentException("Can not route nets with " + outpinCount + " outpins");
            }
            NetPin outpin = netToRoute.NetPins.First(np => np is NetOutpin);

            // start to route from here
            List<Location> startLocations = new List<Location>();
            List<Location> targetLocations = new List<Location>();

            // route from outpin
            string startTileName = netlist.GetInstanceByName(outpin.InstanceName).Location;
            Tile startTile = FPGA.FPGA.Instance.GetTile(startTileName);
            Slice startSlice = startTile.GetSliceByName(netlist.GetInstanceByName(outpin.InstanceName).SliceName);
            Port startPip = startSlice.PortMapping.Ports.Where(p => p.Name.EndsWith(outpin.SlicePort)).First();
            Location outpinLocation = new Location(startTile, startPip);
            startLocations.Add(outpinLocation);

            Queue<Location> targetQueue = new Queue<Location>(targetLocations);
            foreach (NetPin inpin in netToRoute.NetPins.Where(np => np is NetInpin).OrderBy(np => np.InstanceName))
            {
                string targetTileName = netlist.GetInstanceByName(inpin.InstanceName).Location;
                Tile targetTile = FPGA.FPGA.Instance.GetTile(targetTileName);
                Slice targetSlice = targetTile.GetSliceByName(netlist.GetInstanceByName(inpin.InstanceName).SliceName);
                Port targetPip = targetSlice.PortMapping.Ports.Where(p => p.Name.EndsWith(inpin.SlicePort)).First();
                Location inpinLocation = new Location(targetTile, targetPip);

                targetQueue.Enqueue(inpinLocation);
            }

            while (targetQueue.Count > 0)
            {
                // start with new routing
                foreach (XDLPip pip in netToRoute.Pips)
                {
                    Tile newStartTile = FPGA.FPGA.Instance.GetTile(pip.Location);
                    startLocations.Add(new Location(newStartTile, new Port(pip.From)));
                }

                // dequeue next target
                Location targetLocation = targetQueue.Dequeue();

                Watch.Start("route");
                List<Location> revPath = Route(startLocations.First(), targetLocation, 100);
                // extend net
                if (revPath != null)
                {
                    XDLNet extension = new XDLNet(revPath);
                    netToRoute.Add(extension);
                }
                Watch.Stop("route");
            }

            // block the added pips
            netToRoute.BlockUsedResources();
        }

        public List<Location> Route(Location startLocation, Location targetLocation, int maxDepth)
        {
            if (startLocation == null)
            {
                throw new ArgumentException("No start locations given");
            }

            // Create new location manager which contains the majority of the logic.
            DijkstraLocationManager locMan = new DijkstraLocationManager(startLocation, targetLocation);
            List<Location> result = locMan.GetShortestPath(startLocation, targetLocation, maxDepth);

            return result;
        }

        public override void Undo()
        {
            throw new NotImplementedException();
        }

        [Parameter(Comment = "The name to add to the macro")]
        public string NetName = "net";

        [Parameter(Comment = "The path search modue (BFS, DFS, A*)")]
        public string SearchMode = "Dijkstra";
    }

    class DijkstraLocationManager
    {
        public DijkstraLocationManager(Location start, Location end, bool isDirected = true)
        {
            m_graph = new Graph<Location>();
            m_locKeys = new Dictionary<Location, uint>();

            m_startNodeKey = m_graph.AddNode(start);
            m_startNode = start;

            m_targetNodeKey = m_graph.AddNode(end);
            m_targetNode = end;

            m_locKeys.Add(start, m_startNodeKey);
            m_locKeys.Add(end, m_targetNodeKey);

            var locations = FPGA.FPGA.Instance.GetAllLocationsInSelection();

            if (locations.Count() == 0 || !locations.Contains(m_startNode) || !locations.Contains(m_targetNode))
            {
                int startX = Math.Min(m_startNode.Tile.TileKey.X, m_targetNode.Tile.TileKey.X);
                int endX = Math.Max(m_startNode.Tile.TileKey.X, m_targetNode.Tile.TileKey.X);
                int diffX = endX - startX;

                int startY = Math.Min(m_startNode.Tile.TileKey.Y, m_targetNode.Tile.TileKey.Y);
                int endY = Math.Max(m_startNode.Tile.TileKey.Y, m_targetNode.Tile.TileKey.Y);
                int diffY = endY - startY;

                int x1 = startX - diffX;
                int x2 = endX + diffX;
                int y1 = startY - diffY;
                int y2 = endY + diffY;

                if(diffX*diffY >= 4)
                {
                    Console.WriteLine("The area that you are about to search is very large. Execution time will be long and can take upwards of 10GB of RAM.\nAre you sure you want to continue? (Y/N)");
                    var response = Console.ReadKey(true);
                    if (response.KeyChar.ToString().ToLower() != "y")
                    {
                        Console.WriteLine();
                        throw new Exception("The process was aborted by the user.");
                    }
                }

                AddToSelectionXY selectionCmd = new AddToSelectionXY(x1, y1, x2, y2);
                selectionCmd.Do();
                ExpandSelection expandCmd = new ExpandSelection();
                expandCmd.Do();
            }

            List<Tuple<Location, Location, double>> locPairs = new List<Tuple<Location, Location, double>>();

            foreach (Location loc in FPGA.FPGA.Instance.GetAllLocationsInSelection())
            {
                if (!m_locKeys.ContainsKey(loc))
                {
                    m_locKeys.Add(loc, m_graph.AddNode(loc));
                }

                Tile fromTile = loc.Tile;
                WireList wireList = fromTile.WireList;

                foreach (Wire w in wireList)
                {
                    Location toLoc = new Location(fromTile.GetTileAtWireEnd(w), new Port(w.PipOnOtherTile));
                    if (!m_locKeys.ContainsKey(toLoc))
                    {
                        m_locKeys.Add(toLoc, m_graph.AddNode(toLoc));
                    }
                    locPairs.Add(new Tuple<Location, Location, double>(loc, toLoc, w.Cost));
                }
            }

            Random random = new Random();
            foreach (Tuple<Location, Location, double> locTuple in locPairs)
            {
                m_graph.Connect(m_locKeys[locTuple.Item1], m_locKeys[locTuple.Item2], locTuple.Item3); // proof of concept. random edge weighting.
            }

            isInitialised = true;
            Console.WriteLine("Graph initialised.");
        }

        private double GetRandomNumber(double minimum, double maximum, Random random)
        {
            return random.NextDouble() * (maximum - minimum) + minimum;
        }

        public List<Location> GetShortestPath(Location from, Location to, int depth)
        {
            DijkstraResult<Location> result = m_graph.FindPath(m_startNodeKey, m_targetNodeKey);

            //DoPostSearchTasks();
            List<Location> path = result.Path;
            //Console.WriteLine(result.GetPathString());
            return path;
        }

        public uint AddToGraph(Location locToAdd)
        {
            uint key = m_graph.AddNode(locToAdd);
            m_locKeys.Add(locToAdd, key);
            return key;
        }

        /*
        private bool FollowPort(Tile t, Port p)
        {
            if (t.IsPortBlocked(p))
            {
                if (t.IsPortBlocked(p, Tile.BlockReason.Stopover))
                    return true;

                return false;
            }

            return true;
        }
        */

        private bool isInitialised = false;
        private int m_maxDist = 20;
        private Location m_startNode;
        private Location m_targetNode;
        private uint m_startNodeKey;
        private uint m_targetNodeKey;
        private Graph<Location> m_graph;
        private Dictionary<Location, uint> m_locKeys = new Dictionary<Location, uint>();
    }
}