// Copyright (c) 2017 Kastellanos Nikolaos

/* Original source Farseer Physics Engine:
 * Copyright (c) 2014 Ian Qvist, http://farseerphysics.codeplex.com
 * Microsoft Permissive License (Ms-PL) v1.1
 */

/*
* Farseer Physics Engine:
* Copyright (c) 2012 Ian Qvist
*
* Original source Box2D:
* Copyright (c) 2006-2011 Erin Catto http://www.box2d.org
*
* This software is provided 'as-is', without any express or implied
* warranty.  In no event will the authors be held liable for any damages
* arising from the use of this software.
* Permission is granted to anyone to use this software for any purpose,
* including commercial applications, and to alter it and redistribute it
* freely, subject to the following restrictions:
* 1. The origin of this software must not be misrepresented; you must not
* claim that you wrote the original software. If you use this software
* in a product, an acknowledgment in the product documentation would be
* appreciated but is not required.
* 2. Altered source versions must be plainly marked as such, and must not be
* misrepresented as being the original software.
* 3. This notice may not be removed or altered from any source distribution.
*/

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;
using Microsoft.Extensions.ObjectPool;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Collision;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Dynamics.Contacts;
using Robust.Shared.Physics.Events;
using Robust.Shared.Utility;

namespace Robust.Shared.Physics.Systems;

public abstract partial class SharedPhysicsSystem
{
    // TODO: Jesus we should really have a test for this
    /// <summary>
    ///     Ordering is under <see cref="ShapeType"/>
    ///     uses enum to work out which collision evaluation to use.
    /// </summary>
    private static Contact.ContactType[,] _registers =
    {
       {
           // Circle register
           Contact.ContactType.Circle,
           Contact.ContactType.EdgeAndCircle,
           Contact.ContactType.PolygonAndCircle,
           Contact.ContactType.ChainAndCircle,
       },
       {
           // Edge register
           Contact.ContactType.EdgeAndCircle,
           Contact.ContactType.NotSupported, // Edge
           Contact.ContactType.EdgeAndPolygon,
           Contact.ContactType.NotSupported, // Chain
       },
       {
           // Polygon register
           Contact.ContactType.PolygonAndCircle,
           Contact.ContactType.EdgeAndPolygon,
           Contact.ContactType.Polygon,
           Contact.ContactType.ChainAndPolygon,
       },
       {
           // Chain register
           Contact.ContactType.ChainAndCircle,
           Contact.ContactType.NotSupported, // Edge
           Contact.ContactType.ChainAndPolygon,
           Contact.ContactType.NotSupported, // Chain
       }
   };

    private int ContactCount => _activeContacts.Count;

    private List<Contact> _contacts = new(ContactPoolInitialSize);

    private const int ContactPoolInitialSize = 128;
    private const int ContactsPerThread = 32;

    private ObjectPool<Contact> _contactPool = default!;

    private readonly LinkedList<Contact> _activeContacts = new();

    private sealed class ContactPoolPolicy : IPooledObjectPolicy<Contact>
    {
        private readonly SharedDebugPhysicsSystem _debugPhysicsSystem;
        private readonly IManifoldManager _manifoldManager;

        public ContactPoolPolicy(SharedDebugPhysicsSystem debugPhysicsSystem, IManifoldManager manifoldManager)
        {
            _debugPhysicsSystem = debugPhysicsSystem;
            _manifoldManager = manifoldManager;
        }

        public Contact Create()
        {
            var contact = new Contact
            {
                Manifold = new Manifold
                {
                    Points = new ManifoldPoint[2]
                }
            };

            return contact;
        }

        public bool Return(Contact obj)
        {
            SetContact(obj, null, 0, null, 0);
            return true;
        }
    }

    private static void SetContact(Contact contact, Fixture? fixtureA, int indexA, Fixture? fixtureB, int indexB)
    {
        contact.Enabled = true;
        contact.IsTouching = false;
        contact.Flags = ContactFlags.None;
        // TOIFlag = false;

        contact.FixtureA = fixtureA;
        contact.FixtureB = fixtureB;

        contact.ChildIndexA = indexA;
        contact.ChildIndexB = indexB;

        contact.Manifold.PointCount = 0;

        //FPE: We only set the friction and restitution if we are not destroying the contact
        if (fixtureA != null && fixtureB != null)
        {
            contact.Friction = MathF.Sqrt(fixtureA.Friction * fixtureB.Friction);
            contact.Restitution = MathF.Max(fixtureA.Restitution, fixtureB.Restitution);
        }

        contact.TangentSpeed = 0;
    }

    private void InitializeContacts()
    {
        _contactPool = new DefaultObjectPool<Contact>(
            new ContactPoolPolicy(_debugPhysics, _manifoldManager),
            4096);

        InitializePool();
    }

    private void InitializePool()
    {
        var dummy = new Contact[ContactPoolInitialSize];

        for (var i = 0; i < ContactPoolInitialSize; i++)
        {
            dummy[i] = _contactPool.Get();
        }

        for (var i = 0; i < ContactPoolInitialSize; i++)
        {
            _contactPool.Return(dummy[i]);
        }
    }

    private Contact CreateContact(Fixture fixtureA, int indexA, Fixture fixtureB, int indexB)
    {
        var type1 = fixtureA.Shape.ShapeType;
        var type2 = fixtureB.Shape.ShapeType;

        DebugTools.Assert(ShapeType.Unknown < type1 && type1 < ShapeType.TypeCount);
        DebugTools.Assert(ShapeType.Unknown < type2 && type2 < ShapeType.TypeCount);

        // Pull out a spare contact object
        var contact = _contactPool.Get();

        // Edge+Polygon is non-symmetrical due to the way Erin handles collision type registration.
        if ((type1 >= type2 || (type1 == ShapeType.Edge && type2 == ShapeType.Polygon)) && !(type2 == ShapeType.Edge && type1 == ShapeType.Polygon))
        {
            SetContact(contact, fixtureA, indexA, fixtureB, indexB);
        }
        else
        {
            SetContact(contact, fixtureB, indexB, fixtureA, indexA);
        }

        contact.Type = _registers[(int)type1, (int)type2];

        return contact;
    }

    /// <summary>
    /// Try to create a contact between these 2 fixtures.
    /// </summary>
    internal void AddPair(Fixture fixtureA, int indexA, Fixture fixtureB, int indexB, ContactFlags flags = ContactFlags.None)
    {
        PhysicsComponent bodyA = fixtureA.Body;
        PhysicsComponent bodyB = fixtureB.Body;

        // Broadphase has already done the faster check for collision mask / layers
        // so no point duplicating

        // Does a contact already exist?
        if (fixtureA.Contacts.ContainsKey(fixtureB))
            return;

        DebugTools.Assert(!fixtureB.Contacts.ContainsKey(fixtureA));

        // Does a joint override collision? Is at least one body dynamic?
        if (!ShouldCollide(bodyB, bodyA, fixtureA, fixtureB))
            return;

        // Call the factory.
        var contact = CreateContact(fixtureA, indexA, fixtureB, indexB);
        contact.Flags = flags;

        // Contact creation may swap fixtures.
        fixtureA = contact.FixtureA!;
        fixtureB = contact.FixtureB!;
        bodyA = fixtureA.Body;
        bodyB = fixtureB.Body;

        // Insert into world
        _activeContacts.AddLast(contact.MapNode);

        // Connect to body A
        DebugTools.Assert(!fixtureA.Contacts.ContainsKey(fixtureB));
        fixtureA.Contacts.Add(fixtureB, contact);
        bodyA.Contacts.AddLast(contact.BodyANode);

        // Connect to body B
        DebugTools.Assert(!fixtureB.Contacts.ContainsKey(fixtureA));
        fixtureB.Contacts.Add(fixtureA, contact);
        bodyB.Contacts.AddLast(contact.BodyBNode);
    }

    /// <summary>
    ///     Go through the cached broadphase movement and update contacts.
    /// </summary>
    internal void AddPair(in FixtureProxy proxyA, in FixtureProxy proxyB)
    {
        AddPair(proxyA.Fixture, proxyA.ChildIndex, proxyB.Fixture, proxyB.ChildIndex);
    }

    internal static bool ShouldCollide(Fixture fixtureA, Fixture fixtureB)
    {
        return !((fixtureA.CollisionMask & fixtureB.CollisionLayer) == 0x0 &&
                 (fixtureB.CollisionMask & fixtureA.CollisionLayer) == 0x0);
    }

    public void DestroyContact(Contact contact)
    {
        Fixture fixtureA = contact.FixtureA!;
        Fixture fixtureB = contact.FixtureB!;
        PhysicsComponent bodyA = fixtureA.Body;
        PhysicsComponent bodyB = fixtureB.Body;
        var aUid = bodyA.Owner;
        var bUid = bodyB.Owner;

        if (contact.IsTouching)
        {
            var ev1 = new EndCollideEvent(fixtureA, fixtureB);
            var ev2 = new EndCollideEvent(fixtureB, fixtureA);
            RaiseLocalEvent(aUid, ref ev1);
            RaiseLocalEvent(bUid, ref ev2);
        }

        if (contact.Manifold.PointCount > 0 && contact.FixtureA?.Hard == true && contact.FixtureB?.Hard == true)
        {
            if (bodyA.CanCollide)
                SetAwake(aUid, bodyA, true);

            if (bodyB.CanCollide)
                SetAwake(bUid, bodyB, true);
        }

        // Remove from the world
        _activeContacts.Remove(contact.MapNode);

        // Remove from body 1
        DebugTools.Assert(fixtureA.Contacts.ContainsKey(fixtureB));
        fixtureA.Contacts.Remove(fixtureB);
        DebugTools.Assert(bodyA.Contacts.Contains(contact.BodyANode!.Value));
        bodyA.Contacts.Remove(contact.BodyANode);

        // Remove from body 2
        DebugTools.Assert(fixtureB.Contacts.ContainsKey(fixtureA));
        fixtureB.Contacts.Remove(fixtureA);
        bodyB.Contacts.Remove(contact.BodyBNode);

        // Insert into the pool.
        _contactPool.Return(contact);
    }

    private void CollideContacts()
    {
        // Can be changed while enumerating
        // TODO: check for null instead?
        // Work out which contacts are still valid before we decide to update manifolds.
        var node = _activeContacts.First;
        var xformQuery = GetEntityQuery<TransformComponent>();

        while (node != null)
        {
            var contact = node.Value;
            node = node.Next;

            Fixture fixtureA = contact.FixtureA!;
            Fixture fixtureB = contact.FixtureB!;
            int indexA = contact.ChildIndexA;
            int indexB = contact.ChildIndexB;

            PhysicsComponent bodyA = fixtureA.Body;
            PhysicsComponent bodyB = fixtureB.Body;

            // Do not try to collide disabled bodies
            if (!bodyA.CanCollide || !bodyB.CanCollide)
            {
                DestroyContact(contact);
                continue;
            }

            // Is this contact flagged for filtering?
            if ((contact.Flags & ContactFlags.Filter) != 0x0)
            {
                // Check default filtering
                if (!ShouldCollide(fixtureA, fixtureB) ||
                    !ShouldCollide(bodyB, bodyA, fixtureA, fixtureB))
                {
                    DestroyContact(contact);
                    continue;
                }

                // Clear the filtering flag.
                contact.Flags &= ~ContactFlags.Filter;
            }

            bool activeA = bodyA.Awake && bodyA.BodyType != BodyType.Static;
            bool activeB = bodyB.Awake && bodyB.BodyType != BodyType.Static;

            // At least one body must be awake and it must be dynamic or kinematic.
            if (activeA == false && activeB == false)
            {
                continue;
            }

            var xformA = xformQuery.GetComponent(bodyA.Owner);
            var xformB = xformQuery.GetComponent(bodyB.Owner);

            if (xformA.MapUid == null || xformA.MapUid != xformB.MapUid)
            {
                DestroyContact(contact);
                continue;
            }

            // Special-case grid contacts.
            if ((contact.Flags & ContactFlags.Grid) != 0x0)
            {
                var gridABounds = fixtureA.Shape.ComputeAABB(GetPhysicsTransform(bodyA.Owner, xformA, xformQuery), 0);
                var gridBBounds = fixtureB.Shape.ComputeAABB(GetPhysicsTransform(bodyB.Owner, xformB, xformQuery), 0);

                if (!gridABounds.Intersects(gridBBounds))
                {
                    DestroyContact(contact);
                }
                else
                {
                    // Grid contact is still alive.
                    contact.Flags &= ~ContactFlags.Island;
                    _contacts.Add(contact);
                }

                continue;
            }

            var proxyA = fixtureA.Proxies[indexA];
            var proxyB = fixtureB.Proxies[indexB];
            var broadphaseA = xformA.Broadphase?.Uid;
            var broadphaseB = xformB.Broadphase?.Uid;
            var overlap = false;

            // We can have cross-broadphase proxies hence need to change them to worldspace
            if (broadphaseA != null && broadphaseB != null)
            {
                if (broadphaseA == broadphaseB)
                {
                    overlap = proxyA.AABB.Intersects(proxyB.AABB);
                }
                else
                {
                    var proxyAWorldAABB = _transform.GetWorldMatrix(xformQuery.GetComponent(broadphaseA.Value), xformQuery).TransformBox(proxyA.AABB);
                    var proxyBWorldAABB = _transform.GetWorldMatrix(xformQuery.GetComponent(broadphaseB.Value), xformQuery).TransformBox(proxyB.AABB);
                    overlap = proxyAWorldAABB.Intersects(proxyBWorldAABB);
                }
            }

            // Here we destroy contacts that cease to overlap in the broad-phase.
            if (!overlap)
            {
                DestroyContact(contact);
                continue;
            }

            // Contact is actually going to live for manifold generation and solving.
            // This can also short-circuit above for grid contacts.
            contact.Flags &= ~ContactFlags.Island;
            _contacts.Add(contact);
        }

        // Due to the fact some contacts may be removed (and we need to update this array as we iterate).
        // the length may not match the actual contact count, hence we track the index.
        var index = _contacts.Count;
        var status = ArrayPool<ContactStatus>.Shared.Rent(index);
        var worldPoints = ArrayPool<Vector2>.Shared.Rent(index);

        // Update contacts all at once.
        BuildManifolds(_contacts, index, status, worldPoints);

        // Single-threaded so content doesn't need to worry about race conditions.
        for (var i = 0; i < _contacts.Count; i++)
        {
            var contact = _contacts[i];

            switch (status[i])
            {
                case ContactStatus.StartTouching:
                {
                    if (!contact.IsTouching) continue;

                    var fixtureA = contact.FixtureA!;
                    var fixtureB = contact.FixtureB!;
                    var bodyA = fixtureA.Body;
                    var bodyB = fixtureB.Body;
                    var worldPoint = worldPoints[i];

                    var ev1 = new StartCollideEvent(fixtureA, fixtureB, worldPoint);
                    var ev2 = new StartCollideEvent(fixtureB, fixtureA, worldPoint);

                    RaiseLocalEvent(bodyA.Owner, ref ev1, true);
                    RaiseLocalEvent(bodyB.Owner, ref ev2, true);
                    break;
                }
                case ContactStatus.Touching:
                    break;
                case ContactStatus.EndTouching:
                {
                    var fixtureA = contact.FixtureA;
                    var fixtureB = contact.FixtureB;

                    // If something under StartCollideEvent potentially nukes other contacts (e.g. if the entity is deleted)
                    // then we'll just skip the EndCollide.
                    if (fixtureA == null || fixtureB == null) continue;

                    var bodyA = fixtureA.Body;
                    var bodyB = fixtureB.Body;

                    var ev1 = new EndCollideEvent(fixtureA, fixtureB);
                    var ev2 = new EndCollideEvent(fixtureB, fixtureA);

                    RaiseLocalEvent(bodyA.Owner, ref ev1);
                    RaiseLocalEvent(bodyB.Owner, ref ev2);
                    break;
                }
                case ContactStatus.NoContact:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        _contacts.Clear();
        ArrayPool<ContactStatus>.Shared.Return(status);
        ArrayPool<Vector2>.Shared.Return(worldPoints);
    }

    private void BuildManifolds(List<Contact> contacts, int count, ContactStatus[] status, Vector2[] worldPoints)
    {
        var wake = ArrayPool<bool>.Shared.Rent(count);

        if (count > ContactsPerThread * 2)
        {
            var batches = (int) Math.Ceiling((float) count / ContactsPerThread);

            Parallel.For(0, batches, i =>
            {
                var start = i * ContactsPerThread;
                var end = Math.Min(start + ContactsPerThread, count);
                UpdateContacts(contacts, start, end, status, wake, worldPoints);
            });

        }
        else
        {
            UpdateContacts(contacts, 0, count, status, wake, worldPoints);
        }

        // Can't do this during UpdateContacts due to IoC threading issues.
        for (var i = 0; i < count; i++)
        {
            var shouldWake = wake[i];
            if (!shouldWake) continue;

            var contact = contacts[i];
            var bodyA = contact.FixtureA!.Body;
            var bodyB = contact.FixtureB!.Body;
            var aUid = bodyA.Owner;
            var bUid = bodyB.Owner;

            SetAwake(aUid, bodyA, true);
            SetAwake(bUid, bodyB, true);
        }

        ArrayPool<bool>.Shared.Return(wake);
    }

    private void UpdateContacts(List<Contact> contacts, int start, int end, ContactStatus[] status, bool[] wake, Vector2[] worldPoints)
    {
        var xformQuery = GetEntityQuery<TransformComponent>();

        for (var i = start; i < end; i++)
        {
            var contact = contacts[i];
            var uidA = contact.FixtureA!.Body.Owner;
            var uidB = contact.FixtureB!.Body.Owner;
            var bodyATransform = GetPhysicsTransform(uidA, xformQuery.GetComponent(uidA), xformQuery);
            var bodyBTransform = GetPhysicsTransform(uidB, xformQuery.GetComponent(uidB), xformQuery);

            var oldManifold = contact.Manifold;
            var contactStatus = Update(contact, bodyATransform, bodyBTransform, out wake[i]);

#if DEBUG
            _debugPhysics.HandlePreSolve(contact, oldManifold);
#endif

            status[i] = contactStatus;

            if (contactStatus == ContactStatus.StartTouching)
            {
                worldPoints[i] = Physics.Transform.Mul(bodyATransform, contacts[i].Manifold.LocalPoint);
            }
        }
    }

    /// <summary>
        /// Update the contact manifold and touching status.
        /// Note: do not assume the fixture AABBs are overlapping or are valid.
        /// </summary>
        /// <param name="wake">Whether we should wake the bodies due to touching changing.</param>
        /// <returns>What current status of the contact is (e.g. start touching, end touching, etc.)</returns>
        internal ContactStatus Update(Contact contact, Transform bodyATransform, Transform bodyBTransform, out bool wake)
        {
            var oldManifold = contact.Manifold;
            ref var manifold = ref contact.Manifold;

            // Re-enable this contact.
            contact.Enabled = true;

            bool touching;
            var wasTouching = contact.IsTouching;

            wake = false;
            var sensor = contact.IsSensor;

            // Is this contact a sensor?
            if (sensor)
            {
                var shapeA = contact.FixtureA!.Shape;
                var shapeB = contact.FixtureB!.Shape;
                touching = _manifoldManager.TestOverlap(shapeA, contact.ChildIndexA, shapeB, contact.ChildIndexB, bodyATransform, bodyBTransform);

                // Sensors don't generate manifolds.
                manifold.PointCount = 0;
            }
            else
            {
                Evaluate(contact, ref manifold, bodyATransform, bodyBTransform);
                touching = manifold.PointCount > 0;

                // Match old contact ids to new contact ids and copy the
                // stored impulses to warm start the solver.
                for (var i = 0; i < manifold.PointCount; ++i)
                {
                    var mp2 = manifold.Points[i];
                    mp2.NormalImpulse = 0.0f;
                    mp2.TangentImpulse = 0.0f;
                    var id2 = mp2.Id;

                    for (var j = 0; j < oldManifold.PointCount; ++j)
                    {
                        var mp1 = oldManifold.Points[j];

                        if (mp1.Id.Key == id2.Key)
                        {
                            mp2.NormalImpulse = mp1.NormalImpulse;
                            mp2.TangentImpulse = mp1.TangentImpulse;
                            break;
                        }
                    }

                    manifold.Points[i] = mp2;
                }

                if (touching != wasTouching)
                {
                    wake = true;
                }
            }

            contact.IsTouching = touching;
            var status = ContactStatus.NoContact;

            if (!wasTouching)
            {
                if (touching)
                {
                    status = ContactStatus.StartTouching;
                }
            }
            else
            {
                if (!touching)
                {
                    status = ContactStatus.EndTouching;
                }
            }

            return status;
        }

        /// <summary>
        ///     Evaluate this contact with your own manifold and transforms.
        /// </summary>
        /// <param name="manifold">The manifold.</param>
        /// <param name="transformA">The first transform.</param>
        /// <param name="transformB">The second transform.</param>
        private void Evaluate(Contact contact, ref Manifold manifold, in Transform transformA, in Transform transformB)
        {
            var shapeA = contact.FixtureA!.Shape;
            var shapeB = contact.FixtureB!.Shape;

            // This is expensive and shitcodey, see below.
            switch (contact.Type)
            {
                // TODO: Need a unit test for these.
                case Contact.ContactType.Polygon:
                    _manifoldManager.CollidePolygons(ref manifold, (PolygonShape) shapeA, transformA, (PolygonShape) shapeB, transformB);
                    break;
                case Contact.ContactType.PolygonAndCircle:
                    _manifoldManager.CollidePolygonAndCircle(ref manifold, (PolygonShape) shapeA, transformA, (PhysShapeCircle) shapeB, transformB);
                    break;
                case Contact.ContactType.EdgeAndCircle:
                    _manifoldManager.CollideEdgeAndCircle(ref manifold, (EdgeShape) shapeA, transformA, (PhysShapeCircle) shapeB, transformB);
                    break;
                case Contact.ContactType.EdgeAndPolygon:
                    _manifoldManager.CollideEdgeAndPolygon(ref manifold, (EdgeShape) shapeA, transformA, (PolygonShape) shapeB, transformB);
                    break;
                case Contact.ContactType.ChainAndCircle:
                    throw new NotImplementedException();
                    /*
                    ChainShape chain = (ChainShape)FixtureA.Shape;
                    chain.GetChildEdge(_edge, ChildIndexA);
                    Collision.CollisionManager.CollideEdgeAndCircle(ref manifold, _edge, ref transformA, (CircleShape)FixtureB.Shape, ref transformB);
                    */
                case Contact.ContactType.ChainAndPolygon:
                    throw new NotImplementedException();
                    /*
                    ChainShape loop2 = (ChainShape)FixtureA.Shape;
                    loop2.GetChildEdge(_edge, ChildIndexA);
                    Collision.CollisionManager.CollideEdgeAndPolygon(ref manifold, _edge, ref transformA, (PolygonShape)FixtureB.Shape, ref transformB);
                    */
                case Contact.ContactType.Circle:
                    _manifoldManager.CollideCircles(ref manifold, (PhysShapeCircle) shapeA, in transformA, (PhysShapeCircle) shapeB, in transformB);
                    break;
                default:
                    throw new ArgumentOutOfRangeException($"Collision between {shapeA.GetType()} and {shapeB.GetType()} not supported");
            }
        }

    /// <summary>
    ///     Used to prevent bodies from colliding; may lie depending on joints.
    /// </summary>
    private bool ShouldCollide(PhysicsComponent body, PhysicsComponent other, Fixture fixture, Fixture otherFixture)
    {
        if (((body.BodyType & (BodyType.Kinematic | BodyType.Static)) != 0 &&
            (other.BodyType & (BodyType.Kinematic | BodyType.Static)) != 0) ||
            // Kinematic controllers can't collide.
            (body.BodyType == BodyType.KinematicController &&
             other.BodyType == BodyType.KinematicController))
        {
            return false;
        }

        // Does a joint prevent collision?
        // if one of them doesn't have jointcomp then they can't share a common joint.
        // otherwise, only need to iterate over the joints of one component as they both store the same joint.
        if (TryComp(body.Owner, out JointComponent? jointComponentA) &&
            TryComp(other.Owner, out JointComponent? jointComponentB))
        {
            var aUid = jointComponentA.Owner;
            var bUid = jointComponentB.Owner;

            foreach (var joint in jointComponentA.Joints.Values)
            {
                // Check if either: the joint even allows collisions OR the other body on the joint is actually the other body we're checking.
                if (!joint.CollideConnected &&
                    ((aUid == joint.BodyAUid &&
                     bUid == joint.BodyBUid) ||
                    (bUid == joint.BodyAUid &&
                     aUid == joint.BodyBUid))) return false;
            }
        }

        var preventCollideMessage = new PreventCollideEvent(body, other, fixture, otherFixture);
        RaiseLocalEvent(body.Owner, ref preventCollideMessage);

        if (preventCollideMessage.Cancelled) return false;

        preventCollideMessage = new PreventCollideEvent(other, body, otherFixture, fixture);
        RaiseLocalEvent(other.Owner, ref preventCollideMessage);

        if (preventCollideMessage.Cancelled) return false;

        return true;
    }
}

internal enum ContactStatus : byte
{
    NoContact = 0,
    StartTouching = 1,
    Touching = 2,
    EndTouching = 3,
}
