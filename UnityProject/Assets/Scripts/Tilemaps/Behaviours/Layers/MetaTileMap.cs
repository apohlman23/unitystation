﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Messages.Server;
using Objects.Atmospherics;
using UnityEngine;
#if UNITY_EDITOR
using Debug = UnityEngine.Debug;

#endif

namespace TileManagement
{
	[ExecuteInEditMode]
	public class MetaTileMap : MonoBehaviour
	{
		public int TargetMSpreFrame = 5;

		private Stopwatch stopwatch = new Stopwatch();


		private readonly Dictionary<Layer, Dictionary<Vector3Int, TileLocation>> PresentTiles =
			new Dictionary<Layer, Dictionary<Vector3Int, TileLocation>>();

		public Dictionary<Layer, Dictionary<Vector3Int, TileLocation>> PresentTilesNeedsLock => PresentTiles;

		private readonly Dictionary<Layer, Dictionary<Vector3Int, List<TileLocation>>> MultilayerPresentTiles =
			new Dictionary<Layer, Dictionary<Vector3Int, List<TileLocation>>>();

		public Dictionary<Layer, Dictionary<Vector3Int, List<TileLocation>>> MultilayerPresentTilesNeedsLock =>
			MultilayerPresentTiles;

		private Dictionary<Layer, BetterBoundsInt> BoundLocations = new Dictionary<Layer, BetterBoundsInt>();

		/// <summary>
		/// Use this dictionary only if performance isn't critical, otherwise try using arrays below
		/// </summary>This
		public Dictionary<LayerType, Layer> Layers { get; private set; }

		private static readonly Stack<TileLocation> PooledTileLocation = new Stack<TileLocation>();

		private static TileLocation GetPooledTile()
		{
			lock (PooledTileLocation)
			{
				return PooledTileLocation.Count > 0 ? PooledTileLocation.Pop() : new TileLocation();
			}
		}


		public Queue<TileLocation> QueuedChanges = new Queue<TileLocation>();

		//Using arrays for iteration speed
		public LayerType[] LayersKeys { get; private set; }
		public Layer[] LayersValues { get; private set; }
		public ObjectLayer ObjectLayer { get; private set; }

		//Determines the maximum amount of overlays allowed on a tile
		private const int OVERLAY_LIMIT = 20;

		public List<Layer> ffLayersValues;

		/// <summary>
		/// Array of only layers that can ever contain solid stuff
		/// </summary>
		public Layer[] SolidLayersValues { get; private set; }

		/// <summary>
		/// Layers that contain TilemapDamage
		/// </summary>
		public Layer[] DamageableLayers { get; private set; }

		public Matrix matrix = null;

		private Thread mainThread;

		private BetterBoundsInt? LocalCachedBounds;
		public BetterBounds? GlobalCachedBounds;

		[NonSerialized] public Matrix4x4 localToWorldMatrix = Matrix4x4.identity;

		public float Resistance(Vector3Int cellPos, bool includeObjects = true)
		{
			float resistance = 0;
			foreach (var damageableLayer in DamageableLayers)
			{
				resistance += damageableLayer.TilemapDamage.Integrity(cellPos);
			}

			if (includeObjects && ObjectLayer)
			{
				resistance += ObjectLayer.GetObjectResistanceAt(cellPos, true);
			}

			return resistance;
		}

		private void OnEnable()
		{
			Layers = new Dictionary<LayerType, Layer>();
			var layersKeys = new List<LayerType>();
			var layersValues = new List<Layer>();
			var solidLayersValues = new List<Layer>();
			var damageableLayersValues = new List<Layer>();

			foreach (Layer layer in GetComponentsInChildren<Layer>(true))
			{
				layer.metaTileMap = this;
				var type = layer.LayerType;
				Layers[type] = layer;
				layersKeys.Add(type);
				layersValues.Add(layer);
				if (type != LayerType.Effects
				    && type != LayerType.None)
				{
					solidLayersValues.Add(layer);
				}

				if (layer.GetComponent<TilemapDamage>())
				{
					damageableLayersValues.Add(layer);
				}

				if (layer.LayerType == LayerType.Objects)
				{
					ObjectLayer = layer as ObjectLayer;
					continue;
				}

				var InBoundLocations = new BetterBoundsInt( )
				{
					Maximum = Vector3Int.one,
					Minimum = Vector3Int.zero
				};

				if (layer.LayerType != LayerType.Underfloor)
				{
					var ToInsertDictionary = new Dictionary<Vector3Int, TileLocation>();
					BoundsInt bounds = layer.Tilemap.cellBounds;
					TileLocation Tile = null;
					for (int n = bounds.xMin; n < bounds.xMax; n++)
					{
						for (int p = bounds.yMin; p < bounds.yMax; p++)
						{
							Vector3Int localPlace = (new Vector3Int(n, p, 0));
							var getTile = layer.Tilemap.GetTile(localPlace) as LayerTile;
							if (getTile != null)
							{
								Tile = GetPooledTile();
								Tile.position = localPlace;
								Tile.metaTileMap = this;
								Tile.layer = layer;
								Tile.layerTile = getTile;
								Tile.Colour = layer.Tilemap.GetColor(localPlace);
								Tile.transformMatrix = layer.Tilemap.GetTransformMatrix(localPlace);
								ToInsertDictionary[localPlace] = Tile;
								InBoundLocations.ExpandToPoint2D(localPlace);
							}
						}
					}

					lock (PresentTiles)
					{
						PresentTiles[layer] = ToInsertDictionary;
					}
				}

				BoundLocations[layer] = InBoundLocations;
			}

			lock (PresentTiles)
			{
				PresentTiles[ObjectLayer] = new Dictionary<Vector3Int, TileLocation>();
			}




			layersKeys.Sort((layerOne, layerTwo) =>
				layerOne.GetOrder().CompareTo(layerTwo.GetOrder()));

			LayersKeys = layersKeys.ToArray();
			layersValues.Sort((layerOne, layerTwo) =>
				layerOne.LayerType.GetOrder().CompareTo(layerTwo.LayerType.GetOrder()));

			LayersValues = layersValues.ToArray();

			SolidLayersValues = solidLayersValues.ToArray();
			damageableLayersValues.Sort((layerOne, layerTwo) =>
				layerOne.LayerType.GetOrder().CompareTo(layerTwo.LayerType.GetOrder()));

			DamageableLayers = damageableLayersValues.ToArray();
			matrix = GetComponent<Matrix>();
			mainThread = Thread.CurrentThread;
			if (Application.isPlaying)
			{
				UpdateManager.Add(CallbackType.UPDATE, UpdateMe);
			}
		}

		private void OnDisable()
		{
			if (Application.isPlaying)
			{
				UpdateManager.Remove(CallbackType.UPDATE, UpdateMe);
			}
		}

		public void UpdateMe()
		{
			localToWorldMatrix = transform.localToWorldMatrix;
			if (QueuedChanges.Count == 0)
				return;

			stopwatch.Reset();
			stopwatch.Start();
			TileLocation tileLocation = null;
			while (stopwatch.ElapsedMilliseconds < TargetMSpreFrame)
			{
				lock (QueuedChanges)
				{
					if (QueuedChanges.Count == 0)
						break;
					tileLocation = QueuedChanges.Dequeue();
					MainThreadTileChange(tileLocation);
				}
			}

			stopwatch.Stop();

			foreach (var layer in LayersValues)
			{
				layer.overlayStore.Clear();
			}
		}

		private void ApplyTileChange(TileLocation tileLocation)
		{
			lock (QueuedChanges)
			{
				if (QueuedChanges.Contains(tileLocation))
				{
					return;
				}

				//cant modify the unity tilemap in a non main thread
				QueuedChanges.Enqueue(tileLocation);
			}
		}

		public void MainThreadTileChange(TileLocation tileLocation)
		{
			if (tileLocation.layerTile == null)
			{
				MainThreadRemoveTile(tileLocation);
			}
			else
			{
				MainThreadSetTile(tileLocation);
			}
		}

		private void MainThreadRemoveTile(TileLocation tileLocation)
		{
			if (tileLocation.layer.LayerType == LayerType.Floors || tileLocation.layer.LayerType == LayerType.Base)
			{
				tileLocation.layer.TilemapDamage.SwitchObjectsMatrixAt(tileLocation.position);
			}

			//Remove before setting
			if (tileLocation.layer.LayerType == LayerType.Underfloor) //TODO Tile map upgrade
			{
				lock (MultilayerPresentTiles)
				{
					var tileLocations = GetTileLocationsNeedLockSurrounding(tileLocation.position, tileLocation.layer);
					if (tileLocations != null)
					{
						if (tileLocations.Count > Math.Abs(1 - tileLocation.position.z))
						{
							tileLocations[Math.Abs(1 - tileLocation.position.z)] = null;
						}
					}
				}
			}
			else
			{
				lock (PresentTiles)
				{
					PresentTiles[tileLocation.layer][tileLocation.position] = null;
				}
			}

			tileLocation.layer.RemoveTile(tileLocation.position);

			//TODO note Boundaries only recap later when tiles are added outside of it, so therefore it can only increase in size
			// remember update transforms and position and colour when removing On tile map I'm assuming It doesn't clear it?
			// Maybe it sets it to the correct ones when you set a tile idk
			tileLocation.layer.subsystemManager.UpdateAt(tileLocation.position);
			lock (PooledTileLocation)
			{
				PooledTileLocation.Push(tileLocation);
			}

			if (CustomNetworkManager.IsServer)
			{
				if (tileLocation.layer.LayerType == LayerType.Underfloor) //TODO Tilemap upgrade
				{
					matrix.TileChangeManager.AddToChangeList(tileLocation.position, tileLocation.layer.LayerType,
						tileLocation.layer, null, true, true);
				}
				else
				{
					matrix.TileChangeManager.AddToChangeList(tileLocation.position, tileLocation.layer.LayerType,
						tileLocation.layer, null, false, true);
				}

				RemoveTileMessage.Send(matrix.NetworkedMatrix.MatrixSync.netId, tileLocation.position,
					tileLocation.layer.LayerType);
			}

			tileLocation.Clean();
		}

		private void MainThreadSetTile(TileLocation tileLocation)
		{
			tileLocation.layer.SetTile(tileLocation.position, tileLocation.layerTile,
				tileLocation.transformMatrix, tileLocation.Colour);
			tileLocation.layer.subsystemManager.UpdateAt(tileLocation.position);
			if (LocalCachedBounds != null)
			{
				if (LocalCachedBounds.Value.Contains(tileLocation.position) == false)
				{
					var Bounds = LocalCachedBounds.Value; // struct funnies With references
					Bounds.ExpandToPoint2D(tileLocation.position);
					LocalCachedBounds = Bounds;


					GlobalCachedBounds = null;
				}
			}

			if (CustomNetworkManager.IsServer)
			{
				if (tileLocation.layerTile.LayerType == LayerType.Underfloor) //TODO Tilemap upgrade
				{
					matrix.TileChangeManager.AddToChangeList(tileLocation.position,
						tileLocation.layerTile.LayerType, tileLocation.layer, tileLocation, true, false);
				}
				else
				{
					matrix.TileChangeManager.AddToChangeList(tileLocation.position,
						tileLocation.layerTile.LayerType, tileLocation.layer, tileLocation, false, false);
				}


				UpdateTileMessage.Send(matrix.NetworkedMatrix.MatrixSync.netId, tileLocation.position,
					tileLocation.layerTile.TileType, tileLocation.layerTile.name,
					tileLocation.transformMatrix, tileLocation.Colour, tileLocation.layerTile.LayerType);
			}
		}

		/// <summary>
		/// Apply damage to damageable layers, top to bottom.
		/// If tile gets destroyed, remaining damage is applied to the layer below
		/// Returns how much damage was absorbed
		/// </summary>
		public float ApplyDamage(Vector3Int cellPos, float damage, Vector3Int worldPos,
			AttackType attackType = AttackType.Melee)
		{
			float RemainingDamage = damage;
			foreach (var damageableLayer in DamageableLayers)
			{
				if (RemainingDamage <= 0f)
				{
					return (damage);
				}

				RemainingDamage -= damageableLayer.TilemapDamage.ApplyDamage(damage, attackType, worldPos);
			}

			if (RemainingDamage > damage)
			{
				return (damage);
			}

			return (damage - RemainingDamage);
		}

		public bool IsPassableAtOneTileMap(Vector3Int origin, Vector3Int to, bool isServer,
			CollisionType collisionType = CollisionType.Player, bool inclPlayers = true, GameObject context = null,
			List<LayerType> excludeLayers = null, List<TileType> excludeTiles = null, bool ignoreObjects = false,
			bool isReach = false, bool onlyExcludeLayerOnDestination = false)
		{
			// Simple case: orthogonal travel
			if (origin.x == to.x || origin.y == to.y)
			{
				return IsPassableAtOrthogonal(origin, to, isServer, collisionType, inclPlayers, context, excludeLayers,
					excludeTiles, ignoreObjects, isReach: isReach);
			}
			else // diagonal travel
			{
				Vector3Int toX = new Vector3Int(to.x, origin.y, origin.z);
				Vector3Int toY = new Vector3Int(origin.x, to.y, origin.z);

				List<LayerType> diagonalExcludes = onlyExcludeLayerOnDestination ? null : excludeLayers;

				bool isPassableIfHorizontalFirst = IsPassableAtOrthogonal(origin, toX, isServer, collisionType,
					                                   inclPlayers, context, diagonalExcludes,
					                                   excludeTiles, ignoreObjects, isReach: isReach) &&
				                                   IsPassableAtOrthogonal(toX, to, isServer, collisionType, inclPlayers,
					                                   context, excludeLayers, excludeTiles, ignoreObjects,
					                                   isReach: isReach);

				if (isPassableIfHorizontalFirst) return true;

				bool isPassableIfVerticalFirst = IsPassableAtOrthogonal(origin, toY, isServer, collisionType,
					                                 inclPlayers, context, diagonalExcludes,
					                                 excludeTiles, ignoreObjects, isReach: isReach) &&
				                                 IsPassableAtOrthogonal(toY, to, isServer, collisionType, inclPlayers,
					                                 context, excludeLayers, excludeTiles, ignoreObjects,
					                                 isReach: isReach);

				return isPassableIfVerticalFirst;
			}
		}

		private bool IsPassableAtOrthogonal(Vector3Int origin, Vector3Int to, bool isServer,
			CollisionType collisionType = CollisionType.Player, bool inclPlayers = true, GameObject context = null,
			List<LayerType> excludeLayers = null, List<TileType> excludeTiles = null, bool ignoreObjects = false,
			bool isReach = false)
		{
			if (ignoreObjects == false &&
			    ObjectLayer.IsPassableAtOnThisLayer(origin, to, isServer, collisionType, inclPlayers, context,
				    excludeTiles, isReach: isReach) == false)
			{
				return false;
			}

			TileLocation tileLocation = null;
			for (var i = 0; i < SolidLayersValues.Length; i++)
			{
				var solidLayer = SolidLayersValues[i];

				// Skip floor & base collisions if this is not a shuttle
				if (collisionType != CollisionType.Shuttle)
				{
					if ((solidLayer.LayerType == LayerType.Grills || solidLayer.LayerType == LayerType.Tables ||
					     solidLayer.LayerType == LayerType.Walls || solidLayer.LayerType == LayerType.Windows) == false)
					{
						continue;
					}
				}

				// Skip if the current tested layer is being excluded.
				if (excludeLayers != null && excludeLayers.Contains(solidLayer.LayerType))
				{
					continue;
				}

				tileLocation = GetCorrectTileLocationForLayer(to, solidLayer);

				if (tileLocation?.layerTile == null) continue;
				var tile = tileLocation.layerTile as BasicTile;

				// Return passable if the tile type is being excluded from checks.
				if (excludeTiles != null && excludeTiles.Contains(tile.TileType))
					continue;

				if (tile.IsPassable(collisionType, origin, this) == false)
				{
					return false;
				}
			}

			return true;
		}

		public bool IsAtmosPassableAt(Vector3Int position, bool isServer)
		{
			return IsAtmosPassableAt(position, position, isServer);
		}

		public bool IsAtmosPassableAt(Vector3Int origin, Vector3Int to, bool isServer)
		{
			Vector3Int toX = new Vector3Int(to.x, origin.y, origin.z);
			Vector3Int toY = new Vector3Int(origin.x, to.y, origin.z);

			return _IsAtmosPassableAt(origin, toX, isServer) && _IsAtmosPassableAt(toX, to, isServer) ||
			       _IsAtmosPassableAt(origin, toY, isServer) && _IsAtmosPassableAt(toY, to, isServer);
		}

		private bool _IsAtmosPassableAt(Vector3Int origin, Vector3Int to, bool isServer) //TODO needs Object Passable
		{
			if (ObjectLayer.IsAtmosPassableAt(origin, to, isServer) == false)
			{
				return false;
			}


			TileLocation tileLocation = null;
			foreach (var layer in LayersValues)
			{
				if (layer.LayerType == LayerType.Walls || layer.LayerType == LayerType.Windows)
				{
					lock (PresentTiles)
					{
						PresentTiles[layer].TryGetValue(to, out tileLocation);
					}

					if (tileLocation?.layerTile == null) continue;
					if ((tileLocation.layerTile as BasicTile)?.IsAtmosPassable() == false)
					{
						return false;
					}
				}
			}

			return true;
		}

		public bool IsConstructable(Vector3Int position)
		{
			bool canConstruct = false;
			foreach (var layer in LayersValues)
			{
				TileLocation tileLocation = null;
				Dictionary<Vector3Int, TileLocation> tiles;
				if (layer.LayerType == LayerType.Objects)
					continue;

				lock (PresentTiles)
				{
					if (PresentTiles.TryGetValue(layer, out tiles))
					{
						tiles.TryGetValue(position, out tileLocation);
					}
				}

				if (tileLocation?.layerTile == null)
					continue;

				if ((tileLocation.layerTile as BasicTile)?.constructable == false)
				{
					canConstruct = false;
					break;
				}

				canConstruct = true;
			}

			return canConstruct;
		}


		public bool IsSpaceAt(Vector3Int position, bool isServer, bool UseExactForMultilayer = false)
		{
			TileLocation tileLocation = null;
			foreach (var layer in LayersValues)
			{
				if (layer.LayerType == LayerType.Objects) continue;
				if (layer.LayerType == LayerType.Underfloor) continue;
				if (layer.LayerType == LayerType.Tables) continue;
				if (layer.LayerType == LayerType.Effects) continue;

				tileLocation = GetCorrectTileLocationForLayer(position, layer, UseExactForMultilayer);

				if (tileLocation?.layerTile == null) continue;
				if ((tileLocation.layerTile as BasicTile)?.IsSpace() == false)
				{
					return false;
				}
			}

			return true;
		}


		public TileLocation GetCorrectTileLocationForLayer(Vector3Int position, Layer layer,
			bool UseExactForMultilayer = false)
		{
			TileLocation tileLocation = null;
			if (layer.LayerType == LayerType.Underfloor) //TODO Tile map upgrade
			{
				if (UseExactForMultilayer)
				{
					tileLocation = GetTileExactLocationMultilayer(position, layer);
				}
				else
				{
					tileLocation = GetTileLocationMultilayer(position, layer);
				}
			}
			else
			{
				lock (PresentTiles)
				{
					PresentTiles[layer].TryGetValue(position, out tileLocation);
				}
			}

			return tileLocation;
		}

		public bool IsTableAt(Vector3Int position)
		{
			if (Layers.TryGetValue(LayerType.Tables, out var layer))
			{
				TileLocation tileLocation = null;
				lock (PresentTiles)
				{
					PresentTiles[layer].TryGetValue(position, out tileLocation);
				}

				return tileLocation?.layerTile;
			}

			return false;
		}

		public Vector3Int SetTile(Vector3Int position, TileType TileType, string tileName,
			Matrix4x4? matrixTransform = null,
			Color? color = null,
			bool isPlaying = true)
		{
			return SetTile(position, TileManager.GetTile(TileType, tileName), matrixTransform, color, isPlaying);
		}

		public Vector3Int SetTile(Vector3Int position, LayerTile tile, Matrix4x4? matrixTransform = null,
			Color? color = null,
			bool isPlaying = true)
		{
			if (Layers.TryGetValue(tile.LayerType, out var layer))
			{
				if (isPlaying == false) //is the game playing or is this the levelbrush?
				{
					if (tile.LayerType == LayerType.Underfloor) //TODO Tile map upgrade
					{
						for (int i = 0; i < 50; i++)
						{
							position.z = 1 - i;
							if (layer.GetTile(position) == null)
							{
								layer.SetTile(position, tile,
									matrixTransform.GetValueOrDefault(Matrix4x4.identity),
									color.GetValueOrDefault(Color.white));
								return position;
							}
						}

						Logger.LogError(
							"Tile has reached maximum Meta data system depth This could be from accidental placing of multiple tiles",
							Category.Editor);
						return position;
					}
					else
					{
						layer.SetTile(position, tile,
							matrixTransform.GetValueOrDefault(Matrix4x4.identity),
							color.GetValueOrDefault(Color.white));
					}

					return position;
				}


				TileLocation tileLocation = null;

				if (tile.LayerType == LayerType.Underfloor) //TODO Tile map upgrade
				{
					lock (MultilayerPresentTiles)
					{
						var TileLocations = GetTileLocationsNeedLockSurrounding(position, layer);

						int index = FindFirstEmpty(TileLocations);

						position.z = 1 - index;
						if (TileLocations[index] == null)
						{
							TileLocations[index] = GetPooledTile();
							TileLocations[index].layer = layer;
							TileLocations[index].metaTileMap = this;
							TileLocations[index].position = position;
						}

						tileLocation = TileLocations[index];
					}
				}
				else
				{
					lock (PresentTiles)
					{
						PresentTiles[layer].TryGetValue(position, out tileLocation);
					}

					if (tileLocation == null)
					{
						tileLocation = GetPooledTile();
						tileLocation.layer = layer;
						tileLocation.metaTileMap = this;
						tileLocation.position = position;
						lock (PresentTiles)
						{
							PresentTiles[layer][position] = tileLocation;
						}
					}
				}


				tileLocation.layerTile = tile;
				tileLocation.transformMatrix = matrixTransform.GetValueOrDefault(Matrix4x4.identity);
				tileLocation.Colour = color.GetValueOrDefault(Color.white);
				ApplyTileChange(tileLocation);
				return position;
			}
			else
			{
				LogMissingLayer(position, tile.LayerType);
			}

			return position;
		}

		private void LogMissingLayer(Vector3Int position, LayerType layerType)
		{
			Logger.LogErrorFormat("Modifying tile at cellPos {0} for layer type {1} failed because matrix {2} " +
			                      "has no layer of that type. Please add this layer to this matrix in" +
			                      " the scene.", Category.TileMaps, position, layerType, name);
		}

		/// <summary>
		/// Gets the tile with the specified layer type at the specified world position
		/// </summary>
		/// <param name="worldPosition">world position to check</param>
		/// <param name="layerType"></param>
		/// <returns></returns>
		public LayerTile GetTileAtWorldPos(Vector3 worldPosition, LayerType layerType,
			bool UseExactForMultilayer = false)
		{
			return GetTileAtWorldPos(worldPosition.RoundToInt(), layerType, UseExactForMultilayer);
		}

		/// <summary>
		/// Gets the tile with the specified layer type at the specified world position
		/// </summary>
		/// <param name="worldPosition">world position to check</param>
		/// <param name="layerType"></param>
		/// <returns></returns>
		public LayerTile GetTileAtWorldPos(Vector3Int worldPosition, LayerType layerType,
			bool UseExactForMultilayer = false)
		{
			var cellPos = WorldToCell(worldPosition);
			return GetTile(cellPos, layerType, UseExactForMultilayer: UseExactForMultilayer);
		}

		/// <summary>
		/// Gets the tile with the specified layer type at the specified cell position
		/// </summary>
		/// <param name="cellPosition">cell position within the tilemap to get the tile of. NOT the same
		/// as world position.</param>
		/// <param name="layerType"></param>
		/// <returns></returns>
		public LayerTile GetTile(Vector3Int cellPosition, LayerType layerType, bool UseExactForMultilayer = false)
		{
			if (layerType == LayerType.Objects) return null;
			if (Layers.TryGetValue(layerType, out var layer))
			{
				TileLocation tileLocation = null;
				tileLocation = GetCorrectTileLocationForLayer(cellPosition, layer, UseExactForMultilayer);

				return tileLocation?.layerTile;
			}

			LogMissingLayer(cellPosition, layerType);
			return null;
		}


		private TileLocation GetTileExactLocationMultilayer(Vector3Int cellPosition, Layer layer)
		{
			//TODO Tile map upgrade , z Is used as a depth but that needs to be moved to vector4int where it would turn into w
			//This you would just cast to vector3int
			//And use the w for depth Instead of z

			lock (MultilayerPresentTiles)
			{
				var tileLocations = GetTileLocationsNeedLockSurrounding(cellPosition, layer);
				if (tileLocations != null)
				{
					if (tileLocations.Count > Math.Abs(1 - cellPosition.z))
					{
						return tileLocations[Math.Abs(1 - cellPosition.z)];
					}
				}
			}

			return null;
		}

		private List<TileLocation> GetTileLocationsNeedLockSurrounding(Vector3Int cellPosition, Layer layer)
		{
			//TODO Tile map upgrade , z Is used as a depth but that needs to be moved to vector4int where it would turn into w
			var ZZeroposition = cellPosition;
			ZZeroposition.z = 0;
			if (MultilayerPresentTiles.TryGetValue(layer, out var LayerData))
			{
				if (LayerData.TryGetValue(ZZeroposition, out var TileLocations))
				{
					return TileLocations;
				}
				else
				{
					LayerData[ZZeroposition] = new List<TileLocation>();
					return LayerData[ZZeroposition];
				}
			}

			return null;
		}


		private TileLocation GetTileLocationMultilayer(Vector3Int cellPosition, Layer layer)
		{
			//TODO Tile map upgrade , z Is used as a depth but that needs to be moved to vector4int where it would turn into w
			//This you would just cast to vector3int
			//And use the w for depth Instead of z

			lock (MultilayerPresentTiles)
			{
				var tileLocations = GetTileLocationsNeedLockSurrounding(cellPosition, layer);
				if (tileLocations != null)
				{
					for (int i = 0; i < tileLocations.Count; i++)
					{
						if (tileLocations[i] != null && tileLocations[i].layerTile != null)
						{
							return tileLocations[i];
						}
					}
				}
			}

			return null;
		}

		/// <summary>
		/// Gets the colour of the tile with the specified layer type at the specified cell position
		/// </summary>
		/// <param name="cellPosition">cell position within the tilemap to get the tile of. NOT the same
		/// as world position.</param>
		/// <param name="layerType"></param>
		/// <returns></returns>
		public Color? GetColour(Vector3Int cellPosition, LayerType layerType, bool UseExactForMultilayer = false)
		{
			if (layerType == LayerType.Objects) return null;
			if (Layers.TryGetValue(layerType, out var layer))
			{
				TileLocation tileLocation = null;
				tileLocation = GetCorrectTileLocationForLayer(cellPosition, layer, UseExactForMultilayer);
				return tileLocation?.Colour;
			}

			LogMissingLayer(cellPosition, layerType);
			return null;
		}

		/// <summary>
		/// used to check if the tiles are same for networking
		/// </summary>
		/// <param name="position"></param>
		/// <param name="layerTile"></param>
		/// <param name="transformMatrix"></param>
		/// <param name="color"></param>
		/// <returns></returns>
		public bool IsDifferent(Vector3Int cellPosition, LayerTile layerTile, LayerType layerType,
			Matrix4x4? transformMatrix = null,
			Color? color = null, bool UseExactForMultilayer = false)
		{
			if (layerType == LayerType.Objects) return true;
			if (Layers.TryGetValue(layerType, out var layer))
			{
				TileLocation tileLocation = null;
				tileLocation = GetCorrectTileLocationForLayer(cellPosition, layer, UseExactForMultilayer);

				if (tileLocation?.layerTile != layerTile) return true;

				if (color != null)
				{
					if (tileLocation.Colour != color.GetValueOrDefault(Color.white)) return true;
				}

				if (transformMatrix != null)
				{
					if (tileLocation.transformMatrix != transformMatrix.GetValueOrDefault(Matrix4x4.identity))
						return true;
				}

				return false;
				//return layer.IsDifferent(cellPosition, layerTile, transformMatrix, color);
			}

			LogMissingLayer(cellPosition, layerType);
			return true;
		}

		/// <summary>
		/// Gets the topmost tile at the specified cell position
		/// </summary>
		/// <param name="cellPosition">cell position within the tilemap to get the tile of. NOT the same
		/// as world position.</param>
		/// <returns></returns>
		public LayerTile GetTile(Vector3Int cellPosition, bool ignoreEffectsLayer = false,
			bool UseExactForMultilayer = false)
		{
			TileLocation tileLocation = null;
			foreach (var layer in LayersValues)
			{
				if (layer.LayerType == LayerType.Objects) continue;

				if (ignoreEffectsLayer && layer.LayerType == LayerType.Effects) continue;

				tileLocation = GetCorrectTileLocationForLayer(cellPosition, layer, UseExactForMultilayer);

				if (tileLocation != null && tileLocation.layerTile != null)
				{
					break;
				}
			}

			return tileLocation?.layerTile;
		}

		/// <summary>
		/// Gets the topmost tile at the specified cell position , Whilst ignoring the specified tiles in the ExcludedLayers
		/// </summary>
		/// <param name="cellPosition">cell position within the tilemap to get the tile of. NOT the same
		/// as world position.</param>
		/// <returns></returns>
		public LayerTile GetTile(Vector3Int cellPosition, LayerTypeSelection ExcludedLayers,
			bool UseExactForMultilayer = false)
		{
			TileLocation tileLocation = null;
			foreach (var layer in LayersValues)
			{
				if (layer.LayerType == LayerType.Objects) continue;

				if (LTSUtil.IsLayerIn(ExcludedLayers, layer.LayerType)) continue;

				tileLocation = GetCorrectTileLocationForLayer(cellPosition, layer, UseExactForMultilayer);

				if (tileLocation != null)
				{
					break;
				}
			}

			return tileLocation?.layerTile;
		}


		/// <summary>
		/// Checks if tile is empty of objects (only solid by default)
		/// </summary>
		public bool IsEmptyAt(Vector3Int position, bool isServer)
		{
			for (var index = 0; index < LayersValues.Length; index++)
			{
				var layer = LayersValues[index];
				if (layer.LayerType != LayerType.Objects && HasTile(position, layer))
				{
					return false;
				}

				if (layer.LayerType == LayerType.Objects)
				{
					foreach (RegisterTile o in isServer
						? ((ObjectLayer) LayersValues[index]).ServerObjects.Get(position)
						: ((ObjectLayer) LayersValues[index]).ClientObjects.Get(position))
					{
						if (o.IsPassable(isServer) == false)
						{
							return false;
						}
					}
				}
			}

			return true;
		}

		public bool IsNoGravityAt(Vector3Int position, bool isServer)
		{
			for (var i = 0; i < LayersKeys.Length; i++)
			{
				LayerType layer = LayersKeys[i];
				if (layer != LayerType.Objects && HasTile(position, layer))
				{
					return false;
				}

				if (layer == LayerType.Objects)
				{
					foreach (RegisterTile o in isServer
						? ((ObjectLayer) LayersValues[i]).ServerObjects.Get(position)
						: ((ObjectLayer) LayersValues[i]).ClientObjects.Get(position))
					{
						if (o is RegisterObject)
						{
							PushPull pushPull = o.GetComponent<PushPull>();
							if (pushPull == null && o.IsPassable(isServer) == false)
							{
								return false;
							}

							if (pushPull != null && pushPull.CausesGravity())
							{
								return false;
							}
						}
					}
				}
			}

			return true;
		}

		public bool IsEmptyAt(GameObject[] context, Vector3Int position, bool isServer)
		{
			for (var i1 = 0; i1 < LayersKeys.Length; i1++)
			{
				LayerType layer = LayersKeys[i1];
				if (layer != LayerType.Objects && HasTile(position, layer))
				{
					return false;
				}

				if (layer == LayerType.Objects)
				{
					foreach (RegisterTile o in isServer
						? ((ObjectLayer) LayersValues[i1]).ServerObjects.Get(position)
						: ((ObjectLayer) LayersValues[i1]).ClientObjects.Get(position))
					{
						if (o.IsPassable(isServer) == false)
						{
							bool isExcluded = false;
							for (var index = 0; index < context.Length; index++)
							{
								if (o.gameObject == context[index])
								{
									isExcluded = true;
									break;
								}
							}

							if (isExcluded == false)
							{
								return false;
							}
						}
					}
				}
			}

			return true;
		}

		public bool HasObject(Vector3Int position, bool IsServer)
		{
			return ObjectLayer.HasObject(position, IsServer);
		}

		/// <summary>
		/// Cheap method to check if there's a tile, Do not use for objects
		/// </summary>
		/// <param name="position"></param>
		/// <returns></returns>
		public bool HasTile(Vector3Int position, Layer Layer)
		{
			TileLocation tileLocation = null;

			if (Layer.LayerType == LayerType.Objects) return false;
			if (Layer.LayerType == LayerType.Effects) return false;

			tileLocation = GetCorrectTileLocationForLayer(position, Layer);

			return tileLocation?.layerTile;
		}


		/// <summary>
		/// Cheap method to check if there's a tile, Do not use for objects
		/// </summary>
		/// <param name="position"></param>
		/// <returns></returns>
		public bool HasTile(Vector3Int position, bool UseExactForMultilayer = false)
		{
			TileLocation tileLocation = null;
			foreach (var layer in LayersValues)
			{
				if (layer.LayerType == LayerType.Objects) continue;
				if (layer.LayerType == LayerType.Effects) continue;

				tileLocation = GetCorrectTileLocationForLayer(position, layer, UseExactForMultilayer);

				if (tileLocation != null)
				{
					break;
				}
			}

			return tileLocation?.layerTile;
		}

		/// <summary>
		/// ues has object if you want to search for objects this only finds a tile
		/// </summary>
		/// <param name="position"></param>
		/// <param name="layerType"></param>
		/// <returns></returns>
		public bool HasTile(Vector3Int position, LayerType layerType)
		{
			if (layerType == LayerType.Objects)
			{
				Logger.LogError("Please use get objects instead of get tile", Category.TileMaps);
				return false;
			}

			if (Layers.TryGetValue(layerType, out var layer))
			{
				if (layer.LayerType == LayerType.Underfloor)
				{
					return layer.HasTile(position);
				}

				TileLocation tileLocation = null;
				tileLocation = GetCorrectTileLocationForLayer(position, layer);

				if (tileLocation != null)
				{
					return true;
				}

				//return layer.HasTile(position, isServer);
			}
			else
			{
				LogMissingLayer(position, layerType);
			}

			return false;
		}

		/// <summary>
		/// Gets the next free overlay position
		/// </summary>
		/// <param name="position"></param>
		/// <param name="layerType"></param>
		/// <param name="overlayName"></param>
		/// <returns></returns>
		public Vector3Int? GetFreeOverlayPos(Vector3Int position, LayerType layerType)
		{
			if (layerType == LayerType.Objects)
			{
				Logger.LogError("Please use get objects instead of get tile");
				return null;
			}

			TileLocation tileLocation = null;
			position.z = 1;

			if (Layers.TryGetValue(layerType, out var layer))
			{
				//Go through overlays under the overlay limit. The first overlay checked will be at z = 1.
				var count = 0;
				while (count < OVERLAY_LIMIT)
				{
					lock (PresentTiles)
					{
						PresentTiles[layer].TryGetValue(position, out tileLocation);
					}

					if ((tileLocation == null || tileLocation.layerTile == null) &&
					    layer.overlayStore.Contains(position) == false)
					{
						layer.overlayStore.Add(position);
						return position;
					}

					position.z++;
					count++;
				}
			}
			else
			{
				LogMissingLayer(position, layerType);
			}

			return null;
		}

		/// <summary>
		/// Gets all positions with a specific overlay type
		/// </summary>
		/// <param name="position"></param>
		/// <param name="layerType"></param>
		/// <param name="overlayName"></param>
		/// <returns></returns>
		public List<Vector3Int> GetOverlayPosByType(Vector3Int position, LayerType layerType, OverlayType overlayType)
		{
			if (layerType == LayerType.Objects)
			{
				Logger.LogError("Please use get objects instead of get tile");
				return null;
			}

			TileLocation tileLocation = null;
			OverlayTile overlayTile = null;
			List<Vector3Int> pos = new List<Vector3Int>();
			position.z = 1;

			if (Layers.TryGetValue(layerType, out var layer))
			{
				//Go through overlays under the overlay limit. The first overlay checked will be at z = 1.
				var count = 0;
				while (count < OVERLAY_LIMIT)
				{
					lock (PresentTiles)
					{
						PresentTiles[layer].TryGetValue(position, out tileLocation);
					}

					if (tileLocation != null)
					{
						overlayTile = tileLocation.layerTile as OverlayTile;

						if (overlayTile != null && overlayTile.OverlayType == overlayType)
						{
							pos.Add(position);
						}
					}

					position.z++;
					count++;
				}
			}
			else
			{
				LogMissingLayer(position, layerType);
			}

			return pos;
		}

		/// <summary>
		/// Get all overlay positions
		/// </summary>
		/// <param name="position"></param>
		/// <param name="layerType"></param>
		/// <param name="overlayName"></param>
		/// <returns></returns>
		public List<Vector3Int> GetAllOverlayPos(Vector3Int position, LayerType layerType)
		{
			if (layerType == LayerType.Objects)
			{
				Logger.LogError("Please use get objects instead of get tile");
				return null;
			}

			TileLocation tileLocation = null;
			OverlayTile overlayTile = null;
			List<Vector3Int> pos = new List<Vector3Int>();
			position.z = 1;

			if (Layers.TryGetValue(layerType, out var layer))
			{
				//Go through overlays under the overlay limit. The first overlay checked will be at z = 1.
				var count = 0;
				while (count < OVERLAY_LIMIT)
				{
					lock (PresentTiles)
					{
						PresentTiles[layer].TryGetValue(position, out tileLocation);
					}

					if (tileLocation != null)
					{
						overlayTile = tileLocation.layerTile as OverlayTile;

						if (overlayTile != null)
						{
							pos.Add(position);
						}
					}

					position.z++;
					count++;
				}
			}
			else
			{
				LogMissingLayer(position, layerType);
			}

			return pos;
		}

		/// <summary>
		/// Gets all OverlayTiles with a specific overlay type at the cell position
		/// </summary>
		/// <param name="position"></param>
		/// <param name="layerType"></param>
		/// <param name="overlayType"></param>
		/// <returns></returns>
		public List<OverlayTile> GetOverlayTilesByType(Vector3Int position, LayerType layerType,
			OverlayType overlayType)
		{
			if (layerType == LayerType.Objects)
			{
				Logger.LogError("Please use get objects instead of get tile");
				return null;
			}

			TileLocation tileLocation = null;
			OverlayTile overlayTile = null;
			List<OverlayTile> overlayTiles = new List<OverlayTile>();
			position.z = 1;

			if (Layers.TryGetValue(layerType, out var layer))
			{
				//Go through overlays under the overlay limit. The first overlay checked will be at z = 1.
				var count = 0;
				while (count < OVERLAY_LIMIT)
				{
					lock (PresentTiles)
					{
						PresentTiles[layer].TryGetValue(position, out tileLocation);
					}

					if (tileLocation != null)
					{
						overlayTile = tileLocation.layerTile as OverlayTile;

						if (overlayTile != null && overlayTile.OverlayType == overlayType)
						{
							overlayTiles.Add(overlayTile);
						}
					}

					position.z++;
					count++;
				}
			}
			else
			{
				LogMissingLayer(position, layerType);
			}

			return overlayTiles;
		}

		/// <summary>
		/// Whether a tile has this overlay already
		/// </summary>
		public bool HasOverlay(Vector3Int position, LayerType layerType, OverlayTile overlayTileWanted)
		{
			if (layerType == LayerType.Objects)
			{
				Logger.LogError("Please use get objects instead of get tile");
				return false;
			}

			TileLocation tileLocation = null;
			OverlayTile overlayTile = null;
			position.z = 1;

			if (Layers.TryGetValue(layerType, out var layer))
			{
				//Go through overlays under the overlay limit. The first overlay checked will be at z = 1.
				var count = 0;
				while (count < OVERLAY_LIMIT)
				{
					lock (PresentTiles)
					{
						PresentTiles[layer].TryGetValue(position, out tileLocation);
					}

					if (tileLocation != null)
					{
						overlayTile = tileLocation.layerTile as OverlayTile;

						if (overlayTile != null && overlayTile.Equals(overlayTileWanted))
						{
							return true;
						}
					}

					position.z++;
					count++;
				}
			}
			else
			{
				LogMissingLayer(position, layerType);
			}

			return false;
		}

		/// <summary>
		/// Whether a tile has this overlay already
		/// </summary>
		public bool HasOverlayOfType(Vector3Int position, LayerType layerType, OverlayType overlayTypeWanted)
		{
			if (layerType == LayerType.Objects)
			{
				Logger.LogError("Please use get objects instead of get tile");
				return false;
			}

			TileLocation tileLocation = null;
			OverlayTile overlayTile = null;
			position.z = 1;

			if (Layers.TryGetValue(layerType, out var layer))
			{
				//Go through overlays under the overlay limit. The first overlay checked will be at z = 1.
				var count = 0;
				while (count < OVERLAY_LIMIT)
				{
					lock (PresentTiles)
					{
						PresentTiles[layer].TryGetValue(position, out tileLocation);
					}

					if (tileLocation != null)
					{
						overlayTile = tileLocation.layerTile as OverlayTile;

						if (overlayTile != null && overlayTile.OverlayType == overlayTypeWanted)
						{
							return true;
						}
					}

					position.z++;
					count++;
				}
			}
			else
			{
				LogMissingLayer(position, layerType);
			}

			return false;
		}

		//Use TileChangeManager Instead if you want to me networked
		public void RemoveTile(Vector3Int position)
		{
			TileLocation tileLocation = null;
			foreach (var layer in LayersValues)
			{
				if (layer.LayerType == LayerType.Objects) continue;

				if (Application.isPlaying == false)
				{
					if (layer.LayerType == LayerType.Underfloor)
					{
						//TODO Tile map upgrade , xyz z = is the z The level so We need one more xyzw w = what w Coordinate on the z Coordinate on the layer the tile is
						//so, Upgrade messages and the entire system to use vector4int
						//but For now since the z is left hanging is ok
						//If it was vector4int then Use that directly
						var positionnew = position;
						for (int i = 0; i < 50; i++)
						{
							positionnew.z = 1 - i;
							if (layer.RemoveTile(positionnew))
							{
								return;
							}
						}
					}
					else
					{
						if (layer.RemoveTile(position))
						{
							return;
						}
					}

					continue;
				}

				if (layer.LayerType == LayerType.Underfloor) //TODO Tile map upgrade
				{
					tileLocation = GetTileExactLocationMultilayer(position, layer);
				}
				else
				{
					lock (PresentTiles)
					{
						PresentTiles[layer].TryGetValue(position, out tileLocation);
					}
				}

				if (tileLocation != null)
				{
					var refLayer = tileLocation.layerTile.LayerType;
					tileLocation.layerTile = null;
					ApplyTileChange(tileLocation);
					if (refLayer != LayerType.Effects)
					{
						RemoveOverlaysOfType(tileLocation.position, LayerType.Effects, OverlayType.Damage);
					}
					return;
				}
			}
		}

		public void RemoveTileWithlayer(Vector3Int position, LayerType refLayer)
		{
			if (refLayer == LayerType.Objects) return;

			if (Layers.TryGetValue(refLayer, out var layer))
			{
				TileLocation tileLocation = null;

				if (layer.LayerType == LayerType.Underfloor) //TODO Tile map upgrade
				{
					tileLocation = GetTileExactLocationMultilayer(position, layer);
				}
				else
				{
					lock (PresentTiles)
					{
						PresentTiles[layer].TryGetValue(position, out tileLocation);
					}
				}

				if (tileLocation != null)
				{
					tileLocation.layerTile = null;
					ApplyTileChange(tileLocation);
					if (refLayer != LayerType.Effects)
					{
						RemoveOverlaysOfType(tileLocation.position, LayerType.Effects, OverlayType.Damage);
					}
				}
			}
			else
			{
				LogMissingLayer(position, refLayer);
			}
		}

		public Vector3 LocalToWorld(Vector3 localPos) => LayersValues[0].LocalToWorld(localPos);
		public Vector3 CellToWorld(Vector3Int cellPos) => LayersValues[0].CellToWorld(cellPos);
		public Vector3 WorldToLocal(Vector3 worldPos) => LayersValues[0].WorldToLocal(worldPos);

		public BetterBoundsInt GetLocalBounds()
		{
			if (LocalCachedBounds == null)
			{
				CacheLocalBound();
			}

			return LocalCachedBounds.Value;
		}

		public BetterBounds GetWorldBounds()
		{
			if (GlobalCachedBounds == null)
			{
				return CacheGlobalBound();
			}

			return GlobalCachedBounds.Value;
		}

		public void CacheLocalBound()
		{
			Vector3Int minPosition = Vector3Int.one * int.MaxValue;
			Vector3Int maxPosition = Vector3Int.one * int.MinValue;


			foreach (var layerBounds in BoundLocations.Values)
			{
				minPosition = Vector3Int.Min(layerBounds.min, minPosition);
				maxPosition = Vector3Int.Max(layerBounds.max, maxPosition);
			}

			LocalCachedBounds = new BetterBoundsInt()
			{
				Maximum = maxPosition,
				Minimum = minPosition
			};
		}

		public BetterBounds CacheGlobalBound()
		{
			var localBound = GetLocalBounds();

			var offset = new Vector3(0.5f, 0.5f, 0);

			var bottomLeft = localToWorldMatrix.MultiplyPoint(localBound.min + offset);
			var bottomRight = localToWorldMatrix.MultiplyPoint(new Vector3(localBound.xMax, localBound.yMin, 0)  + offset);
			var topLeft = localToWorldMatrix.MultiplyPoint(new Vector3(localBound.xMin, localBound.yMax, 0)  + offset);
			var topRight = localToWorldMatrix.MultiplyPoint(localBound.max  + offset);

			var globalPoints = new Vector3[4] {bottomLeft, bottomRight, topLeft, topRight};
			var minPosition = bottomLeft;
			var maxPosition = bottomLeft;
			foreach (var point in globalPoints)
			{
				minPosition = Vector3.Min(minPosition, point);
				maxPosition = Vector3.Max(maxPosition, point);
			}

			var newGlobalBounds = new BetterBounds()
			{
				Maximum = maxPosition, Minimum = minPosition
			};

			if (matrix.MatrixMove == null ||
			    (CustomNetworkManager.IsServer && matrix.MatrixMove.IsMovingServer == false &&
			     matrix.MatrixMove.IsRotatingServer == false) ||
			    (CustomNetworkManager.IsServer == false && matrix.MatrixMove.IsMovingClient == false &&
			     matrix.MatrixMove.IsRotatingServer == false))
			{
				//Only save the cache if the shuttle is static!
				GlobalCachedBounds = newGlobalBounds;
			}

			return newGlobalBounds;
		}

		public Vector3Int WorldToCell(Vector3 worldPosition)
		{
			return LayersValues[0].WorldToCell(worldPosition);
		}


#if UNITY_EDITOR
		public void SetPreviewTile(Vector3Int position, LayerTile tile, Matrix4x4 transformMatrix)
		{
			for (var i = 0; i < LayersValues.Length; i++)
			{
				Layer layer = LayersValues[i];
				if (layer.LayerType < tile.LayerType)
				{
					if (layer.LayerType == LayerType.Objects) continue;
					Layers[layer.LayerType].SetPreviewTile(position, LayerTile.EmptyTile, Matrix4x4.identity);
				}
			}

			if (Layers.ContainsKey(tile.LayerType) == false)
			{
				Logger.LogErrorFormat($"LAYER TYPE: {0} not found!", Category.TileMaps, tile.LayerType);
				return;
			}

			Layers[tile.LayerType].SetPreviewTile(position, tile, transformMatrix);
		}

		public void ClearPreview()
		{
			for (var i = 0; i < LayersValues.Length; i++)
			{
				if (LayersValues[i].LayerType == LayerType.Objects) continue;
				LayersValues[i].ClearPreview();
			}
		}
#endif

		#region Physics

		//Gets the first hit
		public MatrixManager.CustomPhysicsHit? Raycast(
			Vector2 origin,
			Vector2 direction,
			float distance,
			LayerTypeSelection layerMask, Vector2? To = null,
			LayerTile[] tileNamesToIgnore = null, bool DEBUG = false)
		{
			if (To == null)
			{
				To = direction.normalized * distance;
			}

			if (direction.x == 0 && direction.y == 0)
			{
				direction = (To.Value - origin).normalized;
				distance = (To.Value - origin).magnitude;
			}
#if UNITY_EDITOR
			if (DEBUG)
			{
				var Beginning = (new Vector3((float) origin.x, (float) origin.y, 0).ToWorld(matrix));
				Debug.DrawLine(Beginning + (Vector3.right * 0.09f), Beginning + (Vector3.left * 0.09f), Color.yellow,
					30);
				Debug.DrawLine(Beginning + (Vector3.up * 0.09f), Beginning + (Vector3.down * 0.09f), Color.yellow, 30);

				var end = (new Vector3((float) To.Value.x, (float) To.Value.y, 0).ToWorld(matrix));
				Debug.DrawLine(end + (Vector3.right * 0.09f), end + (Vector3.left * 0.09f), Color.red, 30);
				Debug.DrawLine(end + (Vector3.up * 0.09f), end + (Vector3.down * 0.09f), Color.red, 30);

				Debug.DrawLine(Beginning, end, Color.magenta, 30);
			}
#endif
			double RelativeX = 0;
			double RelativeY = 0;


			double gridOffsetx = 0;
			double gridOffsety = 0;

			int xSteps = 0;
			int ySteps = 0;

			int stepX = 0;
			int stepY = 0;

			double Offsetuntouchx = (origin.x - Math.Round(origin.x));
			double Offsetuntouchy = (origin.y - Math.Round(origin.y));

			if (direction.x < 0)
			{
				gridOffsetx =
					-(-0.5d +
					  Offsetuntouchx); //0.5f  //this is So when you multiply it gives you 0.5 that some tile borders
				stepX = -1; //For detecting which Tile it hits
			}
			else
			{
				gridOffsetx = -0.5d - Offsetuntouchx; //-0.5f
				stepX = 1;
			}

			if (direction.y < 0)
			{
				gridOffsety = -(-0.5d + Offsetuntouchy); // 0.5f
				stepY = -1;
			}
			else
			{
				gridOffsety = -0.5d - Offsetuntouchy; //-0.5f
				stepY = 1;
			}


			var vec = Vector3Int.zero; //Tile it hit Local  Coordinates
			var vecHit = Vector3.zero; //Coordinates of Edge tile hit
			TileLocation tileLocation = null;

			var vexinvX = (1d / (direction.x)); //Editions need to be done here for Working offset
			var vexinvY = (1d / (direction.y)); //Needs to be conditional


			double calculationFloat = 0;

			bool LeftFaceHit = true;


			while (Math.Abs((xSteps + gridOffsetx + stepX) * vexinvX) < distance ||
			       Math.Abs((ySteps + gridOffsety + stepY) * vexinvY) < distance)
			{
				if ((xSteps + gridOffsetx + stepX) * vexinvX < (ySteps + gridOffsety + stepY) * vexinvY
				) // which one has a lesser multiplication factor since that will give a less Magnitude
				{
					xSteps += stepX;

					calculationFloat = ((xSteps + gridOffsetx) * vexinvX);

					RelativeX = direction.x * calculationFloat; //Remove offset here maybe?
					RelativeY = direction.y * calculationFloat;

					LeftFaceHit = true;
				}
				else
				{
					ySteps += stepY;
					calculationFloat = ((ySteps + gridOffsety) * vexinvY);

					RelativeX = direction.x * calculationFloat;
					RelativeY = direction.y * calculationFloat;

					LeftFaceHit = false;
				}


				vec.x = (int) Mathf.Round(origin.x) + xSteps;
				vec.y = (int) Mathf.Round(origin.y) + ySteps;

				vecHit.x = origin.x + (float) RelativeX; //+ offsetX;
				vecHit.y = origin.y + (float) RelativeY; // + offsetY;
				//Check point here

				for (var i = 0; i < LayersValues.Length; i++)
				{
					if (LayersValues[i].LayerType == LayerType.Objects) continue;
					if (LTSUtil.IsLayerIn(layerMask, LayersValues[i].LayerType))
					{
						lock (PresentTiles)
						{
							PresentTiles[LayersValues[i]].TryGetValue(vec, out tileLocation);
						}

#if UNITY_EDITOR
						if (DEBUG)
						{
							var wold = (vecHit.ToWorld(matrix));
							Debug.DrawLine(wold + (Vector3.right * 0.09f), wold + (Vector3.left * 0.09f), Color.green,
								30);
							Debug.DrawLine(wold + (Vector3.up * 0.09f), wold + (Vector3.down * 0.09f), Color.green, 30);

							if (LeftFaceHit)
							{
								Debug.DrawLine(wold + (Vector3.up * 4f), wold + (Vector3.down * 4), Color.blue, 30);
							}
							else
							{
								Debug.DrawLine(wold + (Vector3.right * 4), wold + (Vector3.left * 4), Color.blue, 30);
							}

							ColorUtility.TryParseHtmlString("#ea9335", out var Orange);
							var map = ((Vector3) vec).ToWorld(matrix);
							Debug.DrawLine(map + (Vector3.right * 0.09f), map + (Vector3.left * 0.09f), Orange, 30);
							Debug.DrawLine(map + (Vector3.up * 0.09f), map + (Vector3.down * 0.09f), Orange, 30);
						}
#endif
						if (tileLocation != null)
						{
							if (tileNamesToIgnore != null &&
							    tileNamesToIgnore.Any(c => c.name == tileLocation?.layerTile.name)) continue;

							Vector2 normal;

							if (LeftFaceHit)
							{
								normal = Vector2.left * stepX;
							}
							else
							{
								normal = Vector2.down * stepY;
							}

							Vector3 AdjustedNormal = ((Vector3) normal).ToWorld(matrix);
							AdjustedNormal = AdjustedNormal - (Vector3.zero.ToWorld(matrix));

							return new MatrixManager.CustomPhysicsHit(((Vector3) vec).ToWorld(matrix),
								(vecHit).ToWorld(matrix), AdjustedNormal,
								new Vector2((float) RelativeX, (float) RelativeY).magnitude, tileLocation);
						}
					}
				}
			}

			return null;
		}

		#endregion

		public bool UnderFloorUtilitiesInitialised { get; private set; } = false;

		public void InitialiseUnderFloorUtilities(bool isServer)
		{
			if (Layers.TryGetValue(LayerType.Underfloor, out var layer))
			{
				var ToInsertDictionary = new Dictionary<Vector3Int, List<TileLocation>>();
				BoundsInt bounds = layer.Tilemap.cellBounds;
				TileLocation Tile = null;
				for (int n = bounds.xMin; n < bounds.xMax; n++)
				{
					for (int p = bounds.yMin; p < bounds.yMax; p++)
					{
						Vector3Int localPlace = (new Vector3Int(n, p, 0));
						bool[] PipeDirCheck = new bool[4];

						for (int i = 0; i < 50; i++)
						{
							localPlace.z = 1 - i;
							var getTile = layer.Tilemap.GetTile(localPlace) as LayerTile;
							Tile = null;
							var localPlacezzero = localPlace;
							localPlacezzero.z = 0;
							if (getTile != null)
							{
								Tile = GetPooledTile();
								Tile.position = localPlace;
								Tile.metaTileMap = this;
								Tile.layer = layer;
								Tile.layerTile = getTile;
								Tile.Colour = layer.Tilemap.GetColor(localPlace);
								Tile.transformMatrix = layer.Tilemap.GetTransformMatrix(localPlace);

								if (isServer)
								{
									var electricalCableTile = getTile as ElectricalCableTile;
									if (electricalCableTile != null)
									{
										layer.matrix.AddElectricalNode(new Vector3Int(n, p, localPlace.z),
											electricalCableTile);
									}

									var disposalPipeTile = getTile as Objects.Disposals.DisposalPipe;
									if (disposalPipeTile != null)
									{
										disposalPipeTile.InitialiseNode(localPlace, layer.matrix);
									}

									var pipeTile = getTile as Objects.Atmospherics.PipeTile;
									if (pipeTile != null)
									{
										var matrixStruct =
											layer.matrix.UnderFloorLayer.Tilemap.GetTransformMatrix(localPlace);
										var connection = PipeTile.GetRotatedConnection(pipeTile, matrixStruct);
										var pipeDir = connection.Directions;
										var canInitializePipe = true;
										for (var d = 0; d < pipeDir.Length; d++)
										{
											if (pipeDir[d].Bool)
											{
												if (PipeDirCheck[d])
												{
													canInitializePipe = false;
													Logger.LogWarning(
														$"A pipe is overlapping its connection at ({n}, {p}) in {layer.matrix.gameObject.scene.name} - {layer.matrix.name} with another pipe, removing one",
														Category.Pipes);
													layer.Tilemap.SetTile(localPlace, null);
													break;
												}

												PipeDirCheck[d] = true;
											}
										}

										if (canInitializePipe)
										{
											pipeTile.InitialiseNode(localPlace, layer.matrix);
										}
									}
								}
							}

							if (!ToInsertDictionary.ContainsKey(localPlacezzero))
							{
								ToInsertDictionary[localPlacezzero] = new List<TileLocation>();
							}

							ToInsertDictionary[localPlacezzero].Add(Tile);
						}

						var AlocalPlacezzero = localPlace;
						AlocalPlacezzero.z = 0;
						bool remove = true;
						int LastIndex = 0;
						int L = 0;
						foreach (var TL in ToInsertDictionary[AlocalPlacezzero])
						{
							if (TL != null)
							{
								remove = false;
								LastIndex = L;
							}

							L++;
						}

						if (remove)
						{
							ToInsertDictionary.Remove(AlocalPlacezzero);
						}
						else
						{
							ToInsertDictionary[AlocalPlacezzero].RemoveRange(LastIndex + 1,
								ToInsertDictionary[AlocalPlacezzero].Count - (LastIndex + 1));
						}
					}
				}

				lock (MultilayerPresentTiles)
				{
					MultilayerPresentTiles[layer] = ToInsertDictionary;
				}
			}

			UnderFloorUtilitiesInitialised = true;
		}

		public IEnumerable<T> GetAllTilesByType<T>(Vector3Int position, LayerType LayerType) where T : LayerTile
		{
			List<T> tiles = new List<T>();

			if (Layers.TryGetValue(LayerType, out var layer))
			{
				if (layer.LayerType == LayerType.Underfloor)
				{
					lock (MultilayerPresentTiles)
					{
						var tileLocations = GetTileLocationsNeedLockSurrounding(position, layer);
						if (tileLocations != null)
						{
							foreach (var tileLocation in tileLocations)
							{
								var tile = tileLocation?.layerTile;
								if (tile is T) tiles.Add(tile as T);
							}
						}
					}
				}
				else
				{
					TileLocation tileLocation = null;
					lock (PresentTiles)
					{
						PresentTiles[layer].TryGetValue(position, out tileLocation);
					}

					var tile = tileLocation.layerTile;
					if (tile is T) tiles.Add(tile as T);
				}
			}

			return tiles;
		}


		private int FindFirstEmpty(List<TileLocation> LookThroughList)
		{
			int NewIndex = LookThroughList.Count;
			for (var i = 0; i < NewIndex; i++)
			{
				if (LookThroughList[i]?.layerTile == null)
				{
					return (i);
				}
			}

			LookThroughList.Add(null);
			return (NewIndex);
		}

		public Matrix4x4? GetMatrix4x4(Vector3Int cellPosition, LayerType layerType, bool UseExactForMultilayer = false)
		{
			if (layerType == LayerType.Objects) return null;
			if (Layers.TryGetValue(layerType, out var layer))
			{
				TileLocation tileLocation = null;
				tileLocation = GetCorrectTileLocationForLayer(cellPosition, layer, UseExactForMultilayer);

				return tileLocation?.transformMatrix;
			}
			else
			{
				LogMissingLayer(cellPosition, layerType);
			}

			return null;
		}

		public void RemoveOverlaysOfType(Vector3Int cellPosition, LayerType layerType, OverlayType overlayType,
			bool onlyIfCleanable = false)
		{
			cellPosition.z = 0;

			var overlayPos = GetOverlayPosByType(cellPosition, layerType, overlayType);
			if (overlayPos == null || overlayPos.Count == 0) return;

			foreach (var overlay in overlayPos)
			{
				cellPosition = overlay;

				if (onlyIfCleanable)
				{
					//only remove it if it's a cleanable tile
					var tile = GetTile(cellPosition, layerType) as OverlayTile;
					//it's not an overlay tile or it's not cleanable so don't remove it
					if (tile == null || !tile.IsCleanable) continue;
				}

				RemoveTileWithlayer(cellPosition, layerType);
			}
		}


		/// <summary>
		/// Dynamically adds overlays to tile position
		/// </summary>
		public void AddOverlay(Vector3Int cellPosition, OverlayTile overlayTile, Matrix4x4? transformMatrix = null,
			Color? color = null)
		{
			//use remove methods to remove overlay instead
			if (overlayTile == null) return;

			cellPosition.z = 0;

			//Dont add the same overlay twice
			if (HasOverlay(cellPosition, overlayTile.LayerType, overlayTile)) return;

			var overlayPos = GetFreeOverlayPos(cellPosition, overlayTile.LayerType);
			if (overlayPos == null) return;

			cellPosition = overlayPos.Value;

			SetTile(cellPosition, overlayTile, transformMatrix, color);
		}

		public void AddOverlay(Vector3Int cellPosition, TileType tileType, string tileName,
			Matrix4x4? transformMatrix = null, Color? color = null)
		{
			var overlayTile = TileManager.GetTile(tileType, tileName) as OverlayTile;
			AddOverlay(cellPosition, overlayTile, transformMatrix, color);
		}

		public Color? GetColourOfFirstTile(Vector3Int cellPosition, OverlayType overlayType, LayerType layerType)
		{
			var overlays = GetOverlayPosByType(cellPosition, layerType, overlayType);
			if (overlays.Count == 0) return null;

			return GetColour(overlays.First(), layerType);
		}

		public void RemoveFloorWallOverlaysOfType(Vector3Int cellPosition, OverlayType overlayType,
			bool onlyIfCleanable = false)
		{
			RemoveOverlaysOfType(cellPosition, LayerType.Floors, overlayType, onlyIfCleanable);
			RemoveOverlaysOfType(cellPosition, LayerType.Walls, overlayType, onlyIfCleanable);
		}

		public void RemoveAllOverlays(Vector3Int cellPosition, LayerType layerType, bool onlyIfCleanable = false)
		{
			cellPosition.z = 0;

			var overlayPos = GetAllOverlayPos(cellPosition, layerType);
			if (overlayPos == null || overlayPos.Count == 0) return;

			foreach (var overlay in overlayPos)
			{
				cellPosition = overlay;

				if (onlyIfCleanable)
				{
					//only remove it if it's a cleanable tile
					var tile = GetTile(cellPosition, layerType) as OverlayTile;
					//it's not an overlay tile or it's not cleanable so don't remove it
					if (tile == null || !tile.IsCleanable) continue;
				}

				RemoveTileWithlayer(cellPosition, layerType);
			}
		}

		public bool HasOverlay(Vector3Int cellPosition, TileType tileType, string overlayName)
		{
			var overlayTile = TileManager.GetTile(tileType, overlayName) as OverlayTile;
			if (overlayTile == null) return false;

			return HasOverlay(cellPosition, overlayTile.LayerType, overlayTile);
		}
	}


	public enum OverlayType
	{
		//none is used to say there is no overlay, add new category if you need a new type
		None,
		Gas,
		Damage,
		Cleanable,
		Fire,
		Mining,
		KineticAnimation,
		Plasma,
		NO2,
		WaterVapour,
		Miasma,
		Nitryl,
		Tritium,
		Freon,
		FireSparkles,
		FireOverCharged,
		FireFusion,
		FireRainbow
	}
}