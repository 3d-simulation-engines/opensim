﻿/*
 * Copyright (c) 2011 Intel Corporation
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the Intel Corporation nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Nini.Config;
using log4net;
using OpenSim.Framework;
using OpenSim.Region.Physics.Manager;
using OpenMetaverse;
using OpenSim.Region.Framework;

// TODOs for BulletSim (both BSScene and BSPrim)
// Does NeedsMeshing() really need to exclude all the different shapes?
// Based on material, set density and friction
// More efficient memory usage in passing hull information from BSPrim to BulletSim
// Four states of prim: Physical, regular, phantom and selected. Are we modeling these correctly?
//     In SL one can set both physical and phantom (gravity, does not effect others, makes collisions with ground)
// LinkSets
//     Freeing of memory of linksets in BulletSim::DestroyObject
//     Set child prims phantom since the physicality is handled by the parent prim
//     Linked children need rotation relative to parent (passed as world rotation)
// Should prim.link() and prim.delink() membership checking happen at taint time?
// Pass collision enable flags to BulletSim code so collisions are not reported up unless they are really needed
// Set bouyancy(). Maybe generalize SetFlying() to SetBouyancy() and use the factor to change the gravity effect
// Test sculpties
// Mesh sharing. Use meshHash to tell if we already have a hull of that shape and only create once
// Do attachments need to be handled separately? Need collision events. Do not collide with VolumeDetect
// Parameterize BulletSim. Pass a structure of parameters to the C++ code. Capsule size, friction, ...
// 
namespace OpenSim.Region.Physics.BulletSPlugin
{
public class BSScene : PhysicsScene
{
    private static readonly ILog m_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly string LogHeader = "[BULLETS SCENE]";

    private Dictionary<uint, BSCharacter> m_avatars = new Dictionary<uint, BSCharacter>();
    private Dictionary<uint, BSPrim> m_prims = new Dictionary<uint, BSPrim>();
    private float[] m_heightMap;
    private uint m_worldID;
    public uint WorldID { get { return m_worldID; } }

    public IMesher mesher;
    public int meshLOD = 32;

    private int m_maxSubSteps = 10;
    private float m_fixedTimeStep = 1f / 60f;
    private long m_simulationStep = 0;
    public long SimulationStep { get { return m_simulationStep; } }

    private bool _meshSculptedPrim = true;         // cause scuplted prims to get meshed
    private bool _forceSimplePrimMeshing = false;   // if a cube or sphere, let Bullet do internal shapes
    public float maximumMassObject = 10000.01f;

    public const uint TERRAIN_ID = 0;
    public const uint GROUNDPLANE_ID = 1;

    public float DefaultFriction = 0.70f;
    public float DefaultDensity = 10.000006836f; // Aluminum g/cm3;  TODO: compute based on object material

    public delegate void TaintCallback();
    private List<TaintCallback> _taintedObjects;
    private Object _taintLock = new Object();

    private BulletSimAPI.DebugLogCallback debugLogCallbackHandle;

    public BSScene(string identifier)
    {
    }

    public override void Initialise(IMesher meshmerizer, IConfigSource config)
    {
        if (config != null)
        {
            IConfig pConfig = config.Configs["BulletSim"];
            if (pConfig != null)
            {
                DefaultFriction = pConfig.GetFloat("Friction", DefaultFriction);
                DefaultDensity = pConfig.GetFloat("Density", DefaultDensity);
            }
        }
        // if Debug, enable logging from the unmanaged code
        if (m_log.IsDebugEnabled)
        {
            m_log.DebugFormat("{0}: Initialize: Setting debug callback for unmanaged code", LogHeader);
            debugLogCallbackHandle = new BulletSimAPI.DebugLogCallback(BulletLogger);
            BulletSimAPI.SetDebugLogCallback(debugLogCallbackHandle);
        }

        _meshSculptedPrim = true;           // mesh sculpted prims
        _forceSimplePrimMeshing = false;    // use complex meshing if called for

        _taintedObjects = new List<TaintCallback>();

        mesher = meshmerizer;
        // m_log.DebugFormat("{0}: Initialize: Calling BulletSimAPI.Initialize.", LogHeader);
        m_worldID = BulletSimAPI.Initialize(new Vector3(Constants.RegionSize, Constants.RegionSize, 1000f));
    }

    private void BulletLogger(string msg)
    {
        m_log.Debug("[BULLETS UNMANAGED]:" + msg);
    }

    public override PhysicsActor AddAvatar(string avName, Vector3 position, Vector3 size, bool isFlying)
    {
        m_log.ErrorFormat("{0}: CALL TO AddAvatar in BSScene. NOT IMPLEMENTED", LogHeader);
        return null;
    }

    public override PhysicsActor AddAvatar(uint localID, string avName, Vector3 position, Vector3 size, bool isFlying)
    {
        // m_log.DebugFormat("{0}: AddAvatar: {1}", LogHeader, avName);
        BSCharacter actor = new BSCharacter(localID, avName, this, position, size, isFlying);
        lock (m_avatars) m_avatars.Add(localID, actor);
        return actor;
    }

    public override void RemoveAvatar(PhysicsActor actor)
    {
        // m_log.DebugFormat("{0}: RemoveAvatar", LogHeader);
        if (actor is BSCharacter)
        {
            ((BSCharacter)actor).Destroy();
        }
        try
        {
            lock (m_avatars) m_avatars.Remove(actor.LocalID);
        }
        catch (Exception e)
        {
            m_log.WarnFormat("{0}: Attempt to remove avatar that is not in physics scene: {1}", LogHeader, e);
        }
    }

    public override void RemovePrim(PhysicsActor prim)
    {
        // m_log.DebugFormat("{0}: RemovePrim", LogHeader);
        if (prim is BSPrim)
        {
            ((BSPrim)prim).Destroy();
        }
        try
        {
            lock (m_prims) m_prims.Remove(prim.LocalID);
        }
        catch (Exception e)
        {
            m_log.WarnFormat("{0}: Attempt to remove prim that is not in physics scene: {1}", LogHeader, e);
        }
    }

    public override PhysicsActor AddPrimShape(string primName, PrimitiveBaseShape pbs, Vector3 position,
                                              Vector3 size, Quaternion rotation)  // deprecated
    {
        return null;
    }
    public override PhysicsActor AddPrimShape(string primName, PrimitiveBaseShape pbs, Vector3 position,
                                              Vector3 size, Quaternion rotation, bool isPhysical)
    {
        m_log.ErrorFormat("{0}: CALL TO AddPrimShape in BSScene. NOT IMPLEMENTED", LogHeader);
        return null;
    }

    public override PhysicsActor AddPrimShape(uint localID, string primName, PrimitiveBaseShape pbs, Vector3 position,
                                              Vector3 size, Quaternion rotation, bool isPhysical)
    {
        // m_log.DebugFormat("{0}: AddPrimShape2: {1}", LogHeader, primName);
        IMesh mesh = null;
        if (NeedsMeshing(pbs))
        {
            // if the prim is complex, create the mesh for it.
            // If simple (box or sphere) leave 'mesh' null and physics will do a native shape.
            // m_log.DebugFormat("{0}: AddPrimShape2: creating mesh", LogHeader);
            mesh = mesher.CreateMesh(primName, pbs, size, this.meshLOD, isPhysical);
        }
        BSPrim prim = new BSPrim(localID, primName, this, position, size, rotation, mesh, pbs, isPhysical);
        lock (m_prims) m_prims.Add(localID, prim);
        return prim;
    }

    public override void AddPhysicsActorTaint(PhysicsActor prim) { }

    public override float Simulate(float timeStep)
    {
        int updatedEntityCount;
        IntPtr updatedEntitiesPtr;
        IntPtr[] updatedEntities;
        int collidersCount;
        IntPtr collidersPtr;
        int[] colliders;    // should be uint but Marshal.Copy does not have that overload

        // update the prim states while we know the physics engine is not busy
        ProcessTaints();

        // step the physical world one interval
        m_simulationStep++;
        int numSubSteps = BulletSimAPI.PhysicsStep(m_worldID, timeStep, m_maxSubSteps, m_fixedTimeStep, 
                    out updatedEntityCount, out updatedEntitiesPtr, out collidersCount, out collidersPtr);

        // if there were collisions, they show up here
        if (collidersCount > 0)
        {
            colliders = new int[collidersCount];
            Marshal.Copy(collidersPtr, colliders, 0, collidersCount);
            for (int ii = 0; ii < collidersCount; ii+=2)
            {
                uint cA = (uint)colliders[ii];
                uint cB = (uint)colliders[ii+1];
                SendCollision(cA, cB);
                SendCollision(cB, cA);
            }
        }

        // if any of the objects had updated properties, they are returned in the updatedEntity structure
        if (updatedEntityCount > 0)
        {
            updatedEntities = new IntPtr[updatedEntityCount];
            Marshal.Copy(updatedEntitiesPtr, updatedEntities, 0, updatedEntityCount);
            for (int ii = 0; ii < updatedEntityCount; ii++)
            {
                IntPtr updatePointer = updatedEntities[ii];
                EntityProperties entprop = (EntityProperties)Marshal.PtrToStructure(updatePointer, typeof(EntityProperties));
                // m_log.DebugFormat("{0}: entprop: id={1}, pos={2}", LogHeader, entprop.ID, entprop.Position);
                BSCharacter actor;
                if (m_avatars.TryGetValue(entprop.ID, out actor))
                {
                    actor.UpdateProperties(entprop);
                    continue;
                }
                BSPrim prim;
                if (m_prims.TryGetValue(entprop.ID, out prim))
                {
                    prim.UpdateProperties(entprop);
                }
            }
        }

        // if (collidersCount > 0 || updatedEntityCount > 0) m_log.WarnFormat("{0}: collisions={1}, updates={2}", LogHeader, collidersCount/2, updatedEntityCount);

        return 11f; // returns frames per second
    }

    // Something has collided
    private void SendCollision(uint localID, uint collidingWith)
    {
        if (localID == TERRAIN_ID || localID == GROUNDPLANE_ID)
        {
            // we never send collisions to the terrain
            return;
        }

        ActorTypes type = ActorTypes.Prim;
        if (collidingWith == TERRAIN_ID || collidingWith == GROUNDPLANE_ID)
            type = ActorTypes.Ground;
        else if (m_avatars.ContainsKey(collidingWith))
            type = ActorTypes.Agent;

        BSPrim prim;
        if (m_prims.TryGetValue(localID, out prim)) {
            prim.Collide(collidingWith, type, Vector3.Zero, Vector3.UnitZ, 0.01f);
            return;
        }
        BSCharacter actor;
        if (m_avatars.TryGetValue(localID, out actor)) {
            actor.Collide(collidingWith, type, Vector3.Zero, Vector3.UnitZ, 0.01f);
            return;
        }
        return;
    }

    public override void GetResults() { }

    public override void SetTerrain(float[] heightMap) {
        m_log.DebugFormat("{0}: SetTerrain", LogHeader);
        m_heightMap = heightMap;
        this.TaintedObject(delegate()
        {
            BulletSimAPI.SetHeightmap(m_worldID, m_heightMap);
        });
    }

    public override void SetWaterLevel(float baseheight) { }

    public override void DeleteTerrain() 
    {
        m_log.DebugFormat("{0}: DeleteTerrain()", LogHeader);
    }

    public override void Dispose()
    {
        m_log.DebugFormat("{0}: Dispose()", LogHeader);
    }

    public override Dictionary<uint, float> GetTopColliders()
    {
        return new Dictionary<uint, float>();
    }

    public override bool IsThreaded { get { return false;  } }

    /// <summary>
    /// Routine to figure out if we need to mesh this prim with our mesher
    /// </summary>
    /// <param name="pbs"></param>
    /// <returns>true if the prim needs meshing</returns>
    public bool NeedsMeshing(PrimitiveBaseShape pbs)
    {
        // most of this is redundant now as the mesher will return null if it cant mesh a prim
        // but we still need to check for sculptie meshing being enabled so this is the most
        // convenient place to do it for now...

        // int iPropertiesNotSupportedDefault = 0;

        if (pbs.SculptEntry && !_meshSculptedPrim)
        {
            // m_log.DebugFormat("{0}: NeedsMeshing: scultpy mesh", LogHeader);
            return false;
        }

        // if it's a standard box or sphere with no cuts, hollows, twist or top shear, return false since Bullet 
        // can use an internal representation for the prim
        if (!_forceSimplePrimMeshing)
        {
            // m_log.DebugFormat("{0}: NeedsMeshing: simple mesh: profshape={1}, curve={2}", LogHeader, pbs.ProfileShape, pbs.PathCurve);
            if ((pbs.ProfileShape == ProfileShape.Square && pbs.PathCurve == (byte)Extrusion.Straight)
                || (pbs.ProfileShape == ProfileShape.HalfCircle && pbs.PathCurve == (byte)Extrusion.Curve1
                && pbs.Scale.X == pbs.Scale.Y && pbs.Scale.Y == pbs.Scale.Z))
            {

                if (pbs.ProfileBegin == 0 && pbs.ProfileEnd == 0
                    && pbs.ProfileHollow == 0
                    && pbs.PathTwist == 0 && pbs.PathTwistBegin == 0
                    && pbs.PathBegin == 0 && pbs.PathEnd == 0
                    && pbs.PathTaperX == 0 && pbs.PathTaperY == 0
                    && pbs.PathScaleX == 100 && pbs.PathScaleY == 100
                    && pbs.PathShearX == 0 && pbs.PathShearY == 0)
                {
                    return false;
                }
            }
        }

        /*  TODO: verify that the mesher will now do all these shapes
        if (pbs.ProfileHollow != 0)
            iPropertiesNotSupportedDefault++;

        if ((pbs.PathBegin != 0) || pbs.PathEnd != 0)
            iPropertiesNotSupportedDefault++;

        if ((pbs.PathTwistBegin != 0) || (pbs.PathTwist != 0))
            iPropertiesNotSupportedDefault++; 

        if ((pbs.ProfileBegin != 0) || pbs.ProfileEnd != 0)
            iPropertiesNotSupportedDefault++;

        if ((pbs.PathScaleX != 100) || (pbs.PathScaleY != 100))
            iPropertiesNotSupportedDefault++;

        if ((pbs.PathShearX != 0) || (pbs.PathShearY != 0))
            iPropertiesNotSupportedDefault++;

        if (pbs.ProfileShape == ProfileShape.Circle && pbs.PathCurve == (byte)Extrusion.Straight)
            iPropertiesNotSupportedDefault++;

        if (pbs.ProfileShape == ProfileShape.HalfCircle && pbs.PathCurve == (byte)Extrusion.Curve1 && (pbs.Scale.X != pbs.Scale.Y || pbs.Scale.Y != pbs.Scale.Z || pbs.Scale.Z != pbs.Scale.X))
            iPropertiesNotSupportedDefault++;

        if (pbs.ProfileShape == ProfileShape.HalfCircle && pbs.PathCurve == (byte) Extrusion.Curve1)
            iPropertiesNotSupportedDefault++;

        // test for torus
        if ((pbs.ProfileCurve & 0x07) == (byte)ProfileShape.Square)
        {
            if (pbs.PathCurve == (byte)Extrusion.Curve1)
            {
                iPropertiesNotSupportedDefault++;
            }
        }
        else if ((pbs.ProfileCurve & 0x07) == (byte)ProfileShape.Circle)
        {
            if (pbs.PathCurve == (byte)Extrusion.Straight)
            {
                iPropertiesNotSupportedDefault++;
            }
            // ProfileCurve seems to combine hole shape and profile curve so we need to only compare against the lower 3 bits
            else if (pbs.PathCurve == (byte)Extrusion.Curve1)
            {
                iPropertiesNotSupportedDefault++;
            }
        }
        else if ((pbs.ProfileCurve & 0x07) == (byte)ProfileShape.HalfCircle)
        {
            if (pbs.PathCurve == (byte)Extrusion.Curve1 || pbs.PathCurve == (byte)Extrusion.Curve2)
            {
                iPropertiesNotSupportedDefault++;
            }
        }
        else if ((pbs.ProfileCurve & 0x07) == (byte)ProfileShape.EquilateralTriangle)
        {
            if (pbs.PathCurve == (byte)Extrusion.Straight)
            {
                iPropertiesNotSupportedDefault++;
            }
            else if (pbs.PathCurve == (byte)Extrusion.Curve1)
            {
                iPropertiesNotSupportedDefault++;
            }
        }
        if (iPropertiesNotSupportedDefault == 0)
        {
            return false;
        }
         */
        return true; 
    }

    // The calls to the PhysicsActors can't directly call into the physics engine
    // because it might be busy. We we delay changes to a known time.
    // We rely on C#'s closure to save and restore the context for the delegate.
    public void TaintedObject(TaintCallback callback)
    {
        lock (_taintLock)
            _taintedObjects.Add(callback);
        return;
    }

    // When someone tries to change a property on a BSPrim or BSCharacter, the object queues
    // a callback into itself to do the actual property change. That callback is called
    // here just before the physics engine is called to step the simulation.
    public void ProcessTaints()
    {
        // swizzle a new list into the list location so we can process what's there
        List<TaintCallback> oldList;
        lock (_taintLock)
        {
            oldList = _taintedObjects;
            _taintedObjects = new List<TaintCallback>();
        }

        foreach (TaintCallback callback in oldList)
        {
            try
            {
                callback();
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("{0}: ProcessTaints: Exception: {1}", LogHeader, e);
            }
        }
        oldList.Clear();
    }
}
}
