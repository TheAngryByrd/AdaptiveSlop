// Shares a collection with the main test module: the adaptive graph is
// confined to one owner thread (PLAN.md §7.1), so xUnit must not run this
// module's tests in parallel with the rest of the suite.
[<global.Xunit.Collection("AdaptiveSlop")>]
module AdaptiveSlop.Properties

#nowarn "893"

open System
open System.Collections.Generic
open global.Xunit
open FsCheck
open AdaptiveSlop.Core

// =============================================================================
// Kipo usage shapes (E:\Kipo Pomo.Core\Projections.fs), split into small
// dedicated part tests. Part 1: the entity-scenario cross-map lookup
// (world.Scenarios |> AMap.tryFind inside AMap.mapA, then AMap.choose).
// =============================================================================

[<Fact>]
let ``part 1: cross-map lookup follows the entity-first sequence`` () =
    let entities = CMap.empty<int, int> // entity -> lookup key
    let lookups = CMap.empty<int, int> // key -> value

    // should be empty here
    let contexts =
        entities
        |> AMap.mapA (fun _ key -> AMap.tryFind key lookups)
        |> AMap.chooseV (fun _ v -> v)

    // The shrunk counterexample of the failing property: [0; 2; -1].
    // op 0: upsert entity 0 -> lookup key 0 (lookups empty: no context yet).
    CMap.addOrUpdate 0 0 entities
    Assert.Equal<Map<int, int>>(Map.empty, AMap.toMap contexts)
    // op 2: upsert lookup 0 -> 0 (the context appears).
    CMap.addOrUpdate 0 0 lookups
    Assert.Equal<Map<int, int>>(Map.ofList [ 0, 0 ], AMap.toMap contexts)
    // op -1: remove lookup 0 (the context disappears).
    CMap.remove 0 lookups
    Assert.Equal<Map<int, int>>(Map.empty, AMap.toMap contexts)

// =============================================================================
// FsCheck property tests (FsCheck 3.x API, the built-in runner per the docs:
// Check.QuickThrowOnFailure inside plain xUnit facts).
//
// The reference-impl model tests (MAPA-DESIGN §12) live here; the smoke
// tests below prove the FsCheck machinery runs before the models build on
// it.
// =============================================================================

[<Fact>]
let ``FsCheck smoke: reverse is involutive`` () =
    let revRevIsOrig (xs: int list) = List.rev (List.rev xs) = xs
    Check.QuickThrowOnFailure revRevIsOrig

[<Fact>]
let ``FsCheck smoke: ASet roundtrip`` () =
    let roundtrip (xs: int list) =
        let s = CSet.ofSeq xs
        Set.ofSeq (ASet.force (CSet.value s)) = Set.ofList xs

    Check.QuickThrowOnFailure roundtrip

[<Fact>]
let ``FsCheck smoke: AList append builds the sequence`` () =
    let builds (xs: int list) =
        let l = CList.empty<int>

        for x in xs do
            CList.append x l

        AList.force (CList.value l) = List.toArray xs

    Check.QuickThrowOnFailure builds

// =============================================================================
// Reference-impl model: ASet.mapA (MAPA-DESIGN §12).
//
// The ops are encoded as an int list (no custom generator registration for
// the first model test): kind = op % 3 (0 Add, 1 Remove, 2 Set); the payload
// decodes to an element in [0, 20) and a value in [0, 100). The model tracks
// element -> value plus a value refcount (the output set dedups); the
// library output is compared to the model after EVERY op.
// =============================================================================

[<Fact>]
let ``ASet mapA matches the reference model`` () =
    let prop (ops: int list) =
        let source = CSet.empty<int>
        let values = Dictionary<int, cval<int>>()
        let mapped = ASet.mapA (fun v -> CVal.value values[v]) (CSet.value source)
        // Model: element -> current value; value -> occurrence count; the output.
        let elementValue = Dictionary<int, int>()
        let valueRefs = Dictionary<int, int>()
        let model = HashSet<int>()

        let apply (op: int) =
            let kind = op % 3
            let payload = op / 3
            let element = payload % 20
            let value = (payload / 20) % 100

            match kind with
            | 0 -> // Add the element with a fresh aval holding the value.
                CSet.add element source

                if not (elementValue.ContainsKey element) then
                    values[element] <- CVal.create value
                    elementValue[element] <- value

                    match valueRefs.TryGetValue value with
                    | true, r -> valueRefs[value] <- r + 1
                    | false, _ ->
                        valueRefs[value] <- 1
                        model.Add value |> ignore
            | 1 -> // Remove the element.
                CSet.remove element source

                if elementValue.ContainsKey element then
                    let v = elementValue[element]
                    elementValue.Remove element |> ignore
                    values.Remove element |> ignore
                    let r = valueRefs[v] - 1

                    if r = 0 then
                        valueRefs.Remove v |> ignore
                        model.Remove v |> ignore
                    else
                        valueRefs[v] <- r
            | _ -> // Set the element's aval.
                if elementValue.ContainsKey element then
                    let old = elementValue[element]

                    if old <> value then
                        let r = valueRefs[old] - 1

                        if r = 0 then
                            valueRefs.Remove old |> ignore
                            model.Remove old |> ignore
                        else
                            valueRefs[old] <- r

                        CVal.set value (values[element])
                        elementValue[element] <- value

                        match valueRefs.TryGetValue value with
                        | true, r2 -> valueRefs[value] <- r2 + 1
                        | false, _ ->
                            valueRefs[value] <- 1
                            model.Add value |> ignore

        let mutable ok = true

        for op in ops do
            apply op

            let actual = Set.ofSeq (ASet.toSet mapped)
            let expected = Set.ofSeq model

            if actual <> expected then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

// =============================================================================
// Reference-impl model: AList.mapA (MAPA-DESIGN §12).
//
// Ops encoded as an int list: kind = op % 4 (0 Insert, 1 RemoveAt, 2 Update,
// 3 Set); element = rest % 10; value = (rest / 10) % 100; the position is
// derived at apply time from the current length. The model tracks the
// element list (the output is a list: duplicates survive) plus element ->
// value; a re-inserted element reuses its aval (one aval per element).
// =============================================================================

[<Fact>]
let ``AList mapA matches the reference model`` () =
    let prop (ops: int list) =
        let source = CList.empty<int>
        let values = Dictionary<int, cval<int>>()
        let mapped = AList.mapA (fun v -> CVal.value values[v]) (CList.value source)
        // Model: the element list in order, and the element -> value map.
        let elements = ResizeArray<int>()
        let elementValue = Dictionary<int, int>()

        let apply (op: int) =
            let kind = op % 4
            let rest = op / 4
            let element = rest % 10
            let value = (rest / 10) % 100
            let position = (rest / 1000) % (elements.Count + 1)

            match kind with
            | 0 -> // Insert the element at the position; a fresh element gets a fresh aval.
                if not (elementValue.ContainsKey element) then
                    values[element] <- CVal.create value
                    elementValue[element] <- value

                CList.insertAt position element source
                elements.Insert(position, element)
            | 1 -> // RemoveAt the position.
                if elements.Count > 0 && position < elements.Count then
                    CList.removeAt position source
                    elements.RemoveAt position
            | 2 -> // Update the position with the element.
                if elements.Count > 0 && position < elements.Count then
                    if not (elementValue.ContainsKey element) then
                        values[element] <- CVal.create value
                        elementValue[element] <- value

                    CList.updateAt position element source
                    elements[position] <- element
            | _ -> // Set the element's aval.
                if elementValue.ContainsKey element then
                    CVal.set value (values[element])
                    elementValue[element] <- value

        let mutable ok = true

        for op in ops do
            apply op

            let actual = AList.toArray mapped
            let expected = Array.ofSeq (Seq.map (fun e -> elementValue[e]) elements)

            if actual <> expected then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

// =============================================================================
// Reference-impl model: AMap.mapA.
//
// Ops encoded as an int list: kind = op % 3 (0 AddOrUpdate, 1 Remove,
// 2 Set); key = rest % 10; value = (rest / 10) % 100. A fresh key gets a
// fresh aval; the model tracks key -> value.
// =============================================================================

[<Fact>]
let ``AMap mapA matches the reference model`` () =
    let prop (ops: int list) =
        let source = CMap.empty<int, int>
        let values = Dictionary<int, cval<int>>()
        let mapped = AMap.mapA (fun k _ -> CVal.value values[k]) (CMap.value source)
        let model = Dictionary<int, int>()

        let apply (op: int) =
            let kind = op % 3
            let rest = op / 3
            let key = rest % 10
            let value = (rest / 10) % 100

            match kind with
            | 0 -> // AddOrUpdate the key. The output follows the key's aval:
                // a fresh key gets a fresh aval, an existing key keeps it
                // (the mapping ignores the map value here).
                CMap.addOrUpdate key value source

                if not (model.ContainsKey key) then
                    values[key] <- CVal.create value
                    model[key] <- value
            | 1 -> // Remove the key.
                CMap.remove key source

                if model.ContainsKey key then
                    model.Remove key |> ignore
            | _ -> // Set the key's aval.
                if model.ContainsKey key then
                    CVal.set value (values[key])
                    model[key] <- value

        let mutable ok = true

        for op in ops do
            apply op

            let actual = AMap.toMap mapped
            let expected = Map.ofSeq (seq { for KeyValue(k, v) in model -> k, v })

            if actual <> expected then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

// =============================================================================
// Reference-impl model: ASet.filterA and ASet.chooseA.
//
// Ops encoded as an int list: kind = op % 3 (0 Add, 1 Remove, 2 Set flag);
// element = rest % 10; the flag payload = (rest / 10) % 2. The filter model
// is the membership AND the flag; the choose model maps the element to
// ValueSome value when the flag holds, else ValueNone.
// =============================================================================

[<Fact>]
let ``ASet filterA matches the reference model`` () =
    let prop (ops: int list) =
        let source = CSet.empty<int>
        let flags = Dictionary<int, cval<bool>>()
        let filtered = ASet.filterA (fun v -> CVal.value flags[v]) (CSet.value source)
        let present = HashSet<int>()
        let flagValue = Dictionary<int, bool>()

        let apply (op: int) =
            let kind = op % 3
            let rest = op / 3
            let element = rest % 10
            let flag = (rest / 10) % 2 = 0

            match kind with
            | 0 -> // Add the element with a fresh flag aval.
                CSet.add element source

                if present.Add element then
                    flags[element] <- CVal.create flag
                    flagValue[element] <- flag
            | 1 -> // Remove the element.
                CSet.remove element source
                present.Remove element |> ignore
            | _ -> // Set the element's flag aval.
                if present.Contains element then
                    CVal.set flag (flags[element])
                    flagValue[element] <- flag

        let mutable ok = true

        for op in ops do
            apply op

            let actual = Set.ofSeq (ASet.toSet filtered)

            let expected =
                Set.ofSeq (
                    seq {
                        for e in present do
                            if flagValue[e] then
                                e
                    }
                )

            if actual <> expected then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``ASet chooseA matches the reference model`` () =
    let prop (ops: int list) =
        let source = CSet.empty<int>
        let flags = Dictionary<int, cval<bool>>()

        let chosen =
            ASet.chooseA
                (fun v -> AVal.map (fun f -> if f then Some(v * 10) else None) (CVal.value flags[v]))
                (CSet.value source)

        let present = HashSet<int>()
        let flagValue = Dictionary<int, bool>()

        let apply (op: int) =
            let kind = op % 3
            let rest = op / 3
            let element = rest % 10
            let flag = (rest / 10) % 2 = 0

            match kind with
            | 0 -> // Add the element with a fresh flag aval.
                CSet.add element source

                if present.Add element then
                    flags[element] <- CVal.create flag
                    flagValue[element] <- flag
            | 1 -> // Remove the element.
                CSet.remove element source
                present.Remove element |> ignore
            | _ -> // Set the element's flag aval.
                if present.Contains element then
                    CVal.set flag (flags[element])
                    flagValue[element] <- flag

        let mutable ok = true

        for op in ops do
            apply op

            let actual = Set.ofSeq (ASet.toSet chosen)

            let expected =
                Set.ofSeq (
                    seq {
                        for e in present do
                            if flagValue[e] then
                                e * 10
                    }
                )

            if actual <> expected then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

// =============================================================================
// Reference-impl model: ASet.ofExternal (MAPA-DESIGN §1.1).
//
// Ops encoded as an int list: kind = op % 2 (0 = replace the external
// snapshot, 1 = invalidate). The model: the library re-reads the snapshot
// only when invalidated, on the next read; the expected content is the
// snapshot when the model is dirty, else the last read snapshot.
// =============================================================================

[<Fact>]
let ``ASet ofExternal matches the reference model`` () =
    let prop (ops: int list) =
        let mutable snapshot = HashSet<int>()
        let ext, invalidate = ASet.ofExternal (fun () -> snapshot :> IReadOnlySet<int>)
        // Model: whether an invalidate is pending, and the last read snapshot.
        // The first read always re-reads (the node materializes initially).
        let mutable dirty = true
        let mutable lastSeen = HashSet<int>()

        let apply (op: int) =
            let kind = op % 2
            let rest = op / 2
            let element = rest % 20
            let value = (rest / 20) % 100

            match kind with
            | 0 -> // Replace the external snapshot with a single element.
                snapshot <- HashSet<int>([ value ])

                if element % 4 = 0 then
                    snapshot.Add element |> ignore // sometimes two elements
            | _ -> // Invalidate: the next read re-reads the snapshot.
                invalidate ()
                dirty <- true

            // Read after every op; the expected is the snapshot iff dirty.
            let expected = if dirty then snapshot else lastSeen
            let actual = ASet.toSet ext

            if Set.ofSeq actual <> Set.ofSeq expected then
                false
            else
                dirty <- false
                lastSeen <- HashSet<int>(expected)
                true

        let mutable ok = true

        for op in ops do
            if not (apply op) then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

// =============================================================================
// Kipo usage shapes (E:\Kipo Pomo.Core\Projections.fs):
//
// 1. The derived-stats join: AMap.choose2V over two maps + AMap.mapA.
// 2. The entity-scenario context: AMap.mapA whose mapping does a cross-map
//    lookup (world.Scenarios |> AMap.tryFind), then AMap.choose drops the
//    Nones.
// 3. The live-entities projection: AMap.filter + AMap.keys.
// 4. The physics cache: force the world maps, then compute per-scenario
//    snapshots (the RefreshAllCaches shape).
// 5. The nearby-entities query: cell-based radius query over the forced
//    snapshot vs the brute-force distance filter.
// 6. The combat statuses: AList.choose over the effect list.
// =============================================================================

[<Fact>]
let ``AMap join choose2V with mapA matches the model`` () =
    let prop (ops: int list) =
        let baseStats = CMap.empty<int, int>
        let effects = CMap.empty<int, int>

        let derived =
            (CMap.value baseStats, CMap.value effects)
            ||> AMap.choose2V (fun _ av bv ->
                match struct (av, bv) with
                | ValueSome x, ValueSome y -> ValueSome(struct (x, y))
                | _ -> ValueNone)
            |> AMap.mapA (fun _ struct (x, y) -> AVal.constant (x + y))

        let modelA = Dictionary<int, int>()
        let modelB = Dictionary<int, int>()

        let apply (op: int) =
            let kind = op % 4
            let rest = op / 4
            let key = rest % 10
            let value = (rest / 10) % 100

            match kind with
            | 0 -> // Upsert into the base-stats map.
                CMap.addOrUpdate key value baseStats
                modelA[key] <- value
            | 1 -> // Remove from the base-stats map.
                CMap.remove key baseStats
                modelA.Remove key |> ignore
            | 2 -> // Upsert into the effects map.
                CMap.addOrUpdate key value effects
                modelB[key] <- value
            | _ -> // Remove from the effects map.
                CMap.remove key effects
                modelB.Remove key |> ignore

        let mutable ok = true

        for op in ops do
            apply op

            let actual = AMap.toMap derived

            let expected =
                Map.ofSeq (
                    seq {
                        for KeyValue(k, a) in modelA do
                            match modelB.TryGetValue k with
                            | true, b -> k, a + b
                            | false, _ -> ()
                    }
                )

            if actual <> expected then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``AMap mapA with cross-map lookup matches the model`` () =
    let prop (ops: int list) =
        let entities = CMap.empty<int, int> // entity -> lookup key
        let lookups = CMap.empty<int, int> // key -> value

        let contexts =
            entities
            |> AMap.mapA (fun _ key -> AMap.tryFind key (CMap.value lookups))
            |> AMap.choose (fun _ v -> ValueOption.toOption v)

        let modelEntities = Dictionary<int, int>()
        let modelLookups = Dictionary<int, int>()

        let apply (op: int) =
            let kind = op % 4
            let rest = op / 4
            let key = rest % 10
            let value = (rest / 10) % 100

            match kind with
            | 0 -> // Upsert an entity with its lookup key.
                CMap.addOrUpdate key value entities
                modelEntities[key] <- value
            | 1 -> // Remove the entity.
                CMap.remove key entities
                modelEntities.Remove key |> ignore
            | 2 -> // Upsert a lookup entry.
                CMap.addOrUpdate key value lookups
                modelLookups[key] <- value
            | _ -> // Remove the lookup entry.
                CMap.remove key lookups
                modelLookups.Remove key |> ignore

        let mutable ok = true

        for op in ops do
            apply op

            let actual = AMap.toMap contexts

            let expected =
                Map.ofSeq (
                    seq {
                        for KeyValue(id, lookupKey) in modelEntities do
                            match modelLookups.TryGetValue lookupKey with
                            | true, v -> id, v
                            | false, _ -> ()
                    }
                )

            if actual <> expected then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``ASet mapA with cross-map lookup matches the model`` () =
    let prop (ops: int list) =
        let entities = CSet.empty<int> // entity ids
        let lookups = CMap.empty<int, int> // key -> value

        let contexts =
            entities
            |> ASet.mapA (fun id -> AMap.tryFind id (CMap.value lookups))
            |> ASet.choose (fun v -> ValueOption.toOption v)

        let modelEntities = HashSet<int>()
        let modelLookups = Dictionary<int, int>()

        let apply (op: int) =
            let kind = op % 4
            let rest = op / 4
            let key = rest % 10
            let value = (rest / 10) % 100

            match kind with
            | 0 -> // Add an entity id.
                CSet.add key entities
                modelEntities.Add key |> ignore
            | 1 -> // Remove the entity.
                CSet.remove key entities
                modelEntities.Remove key |> ignore
            | 2 -> // Upsert a lookup entry.
                CMap.addOrUpdate key value lookups
                modelLookups[key] <- value
            | _ -> // Remove the lookup entry.
                CMap.remove key lookups
                modelLookups.Remove key |> ignore

        let mutable ok = true

        for op in ops do
            apply op

            let actual = Set.ofSeq (ASet.toSet contexts)

            let expected =
                Set.ofSeq (
                    seq {
                        for id in modelEntities do
                            match modelLookups.TryGetValue id with
                            | true, v -> v
                            | false, _ -> ()
                    }
                )

            if actual <> expected then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``AList mapA with cross-map lookup matches the model`` () =
    let prop (ops: int list) =
        let entities = CList.empty<int> // entity ids in order
        let lookups = CMap.empty<int, int> // key -> value

        let contexts =
            entities
            |> AList.mapA (fun id -> AMap.tryFind id (CMap.value lookups))
            |> AList.choose (fun v -> ValueOption.toOption v)

        let modelEntities = ResizeArray<int>()
        let modelLookups = Dictionary<int, int>()

        let apply (op: int) =
            let kind = op % 4
            let rest = op / 4
            let key = rest % 10
            let value = (rest / 10) % 100

            let position =
                // F#'s % is signed: normalize to [0, Count + 1).
                let p = (rest / 1000) % (modelEntities.Count + 1)
                if p < 0 then p + (modelEntities.Count + 1) else p

            match kind with
            | 0 -> // Insert the entity id at the position.
                CList.insertAt position key entities
                modelEntities.Insert(position, key)
            | 1 -> // Remove the entity at the position.
                if modelEntities.Count > 0 && position < modelEntities.Count then
                    CList.removeAt position entities
                    modelEntities.RemoveAt position
            | 2 -> // Upsert a lookup entry.
                CMap.addOrUpdate key value lookups
                modelLookups[key] <- value
            | _ -> // Remove the lookup entry.
                CMap.remove key lookups
                modelLookups.Remove key |> ignore

        let mutable ok = true

        for op in ops do
            apply op

            let actual = AList.toArray contexts

            let expected =
                Array.ofSeq (
                    seq {
                        for id in modelEntities do
                            match modelLookups.TryGetValue id with
                            | true, v -> v
                            | false, _ -> ()
                    }
                )

            if actual <> expected then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``AMap filter with keys matches the model`` () =
    let prop (ops: int list) =
        let resources = CMap.empty<int, bool> // entity -> alive flag

        let live = resources |> AMap.filter (fun _ alive -> alive) |> AMap.keys

        let model = Dictionary<int, bool>()

        let apply (op: int) =
            let kind = op % 2
            let rest = op / 2
            let key = rest % 10
            let alive = (rest / 10) % 2 = 0

            match kind with
            | 0 -> // Upsert the entity's alive flag.
                CMap.addOrUpdate key alive resources
                model[key] <- alive
            | _ -> // Remove the entity.
                CMap.remove key resources
                model.Remove key |> ignore

        let mutable ok = true

        for op in ops do
            apply op

            let actual = ASet.toSet live

            let expected =
                Set.ofSeq (
                    seq {
                        for KeyValue(k, alive) in model do
                            if alive then
                                k
                    }
                )

            if Set.ofSeq actual <> expected then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``physics cache refresh matches a fresh computation`` () =
    let prop (ops: int list) =
        let positions = CMap.empty<int, float>
        let velocities = CMap.empty<int, float>
        let inScenario = CSet.empty<int>
        let dt = 0.5

        let modelPos = Dictionary<int, float>()
        let modelVel = Dictionary<int, float>()
        let modelMembers = HashSet<int>()

        // The cache shape: force the world maps, then compute the snapshot.
        let refresh () =
            let pos = AMap.force (CMap.value positions)
            let vel = AMap.force (CMap.value velocities)
            let members = ASet.force (CSet.value inScenario)
            let computed = Dictionary<int, float>()

            for id in members do
                let mutable v = 0.0

                if vel.TryGetValue(id, &v) then
                    computed[id] <- pos[id] + v * dt
                else
                    computed[id] <- pos[id]

            computed

        // The model: the same computation from the model state.
        let model () =
            let computed = Dictionary<int, float>()

            for id in modelMembers do
                let mutable v = 0.0

                if modelVel.TryGetValue(id, &v) then
                    computed[id] <- modelPos[id] + v * dt
                else
                    computed[id] <- modelPos[id]

            computed

        let apply (op: int) =
            let kind = op % 3
            let rest = op / 3
            let id = rest % 10
            let value = float ((rest / 10) % 100)

            match kind with
            | 0 -> // Upsert the position and mark the entity in the scenario.
                CMap.addOrUpdate id value positions
                modelPos[id] <- value
                CSet.add id inScenario
                modelMembers.Add id |> ignore
            | 1 -> // Upsert the velocity.
                CMap.addOrUpdate id value velocities
                modelVel[id] <- value
            | _ -> // Remove the entity from the scenario.
                CSet.remove id inScenario
                modelMembers.Remove id |> ignore

        let mutable ok = true

        for op in ops do
            apply op

            let actual = refresh ()
            let expected = model ()

            if actual.Count <> expected.Count then
                ok <- false
            else
                let mutable mismatch = false

                for KeyValue(id, v) in expected do
                    let mutable a = 0.0

                    if not (actual.TryGetValue(id, &a)) || a <> v then
                        mismatch <- true

                if mismatch then
                    ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``physics cache snapshot agrees with the adaptive derivation`` () =
    let prop (ops: int list) =
        let positions = CMap.empty<int, float>
        let velocities = CMap.empty<int, float>
        let inScenario = CSet.empty<int>
        let dt = 0.5

        // The original adaptive cache (before the snapshot switch): a derived
        // per-member position computed from the adaptive world.
        let derived =
            inScenario
            |> ASet.mapA (fun id ->
                AVal.map2
                    (fun p v -> struct (id, p + v * dt))
                    (AMap.tryFind id (CMap.value positions)
                     |> AVal.map (ValueOption.defaultValue 0.0))
                    (AMap.tryFind id (CMap.value velocities)
                     |> AVal.map (ValueOption.defaultValue 0.0)))

        let modelPos = Dictionary<int, float>()
        let modelVel = Dictionary<int, float>()
        let modelMembers = HashSet<int>()

        // The snapshot cache shape: force the world maps, then compute.
        let snapshot () =
            let pos = AMap.force (CMap.value positions)
            let vel = AMap.force (CMap.value velocities)
            let members = ASet.force (CSet.value inScenario)
            let computed = Dictionary<int, float>()

            for id in members do
                let mutable v = 0.0

                if vel.TryGetValue(id, &v) then
                    computed[id] <- pos[id] + v * dt
                else
                    computed[id] <- pos[id]

            computed

        // The model: the same computation from the model state.
        let model () =
            let computed = Dictionary<int, float>()

            for id in modelMembers do
                let mutable v = 0.0

                if modelVel.TryGetValue(id, &v) then
                    computed[id] <- modelPos[id] + v * dt
                else
                    computed[id] <- modelPos[id]

            computed

        let apply (op: int) =
            let kind = op % 3
            let rest = op / 3
            let id = rest % 10
            let value = float ((rest / 10) % 100)

            match kind with
            | 0 -> // Upsert the position and mark the entity in the scenario.
                CMap.addOrUpdate id value positions
                modelPos[id] <- value
                CSet.add id inScenario
                modelMembers.Add id |> ignore
            | 1 -> // Upsert the velocity.
                CMap.addOrUpdate id value velocities
                modelVel[id] <- value
            | _ -> // Remove the entity from the scenario.
                CSet.remove id inScenario
                modelMembers.Remove id |> ignore

        let mutable ok = true

        for op in ops do
            apply op

            // The adaptive derivation, forced, must equal the snapshot and
            // the model.
            let adaptive =
                ASet.force derived |> Seq.map (fun struct (id, v) -> id, v) |> Map.ofSeq

            let snap = snapshot ()
            let expected = model ()

            if Map.count adaptive <> expected.Count then
                ok <- false
            else
                let mutable mismatch = false

                for KeyValue(id, v) in expected do
                    let mutable a = 0.0
                    let mutable s = 0.0

                    if
                        (not (Map.tryFind id adaptive |> Option.exists (fun x -> x = v)))
                        || (not (snap.TryGetValue(id, &s)) || s <> v)
                    then
                        mismatch <- true

                if mismatch then
                    ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``spatial radius query matches the brute force`` () =
    let prop (ops: int list) =
        let positions = CMap.empty<int, struct (float * float)>
        let live = CSet.empty<int>
        let cellSize = 4.0
        let radius = 5.0
        let center = struct (10.0, 10.0)

        let modelPos = Dictionary<int, struct (float * float)>()
        let modelLive = HashSet<int>()

        // The Kipo shape: cells in radius -> potential targets -> distance
        // filter over the forced snapshot.
        let query () =
            let pos = AMap.force (CMap.value positions)
            let members = ASet.force (CSet.value live)
            let cellRadius = int (radius / cellSize) + 1
            let struct (centerX, centerY) = center
            let struct (ccx, ccy) = struct (int (centerX / cellSize), int (centerY / cellSize))
            let results = ResizeArray<int>()

            for dx = -cellRadius to cellRadius do
                for dy = -cellRadius to cellRadius do
                    let cell = struct (ccx + dx, ccy + dy)
                    let struct (cellX, cellY) = cell
                    let cellOrigin = struct (float cellX * cellSize, float cellY * cellSize)

                    // Only cells whose origin is within radius + cellSize count.
                    let struct (ox, oy) = cellOrigin
                    let dxp = ox - centerX
                    let dyp = oy - centerY

                    if dxp * dxp + dyp * dyp <= (radius + cellSize) * (radius + cellSize) then
                        for id in members do
                            match pos.TryGetValue id with
                            | true, p ->
                                let struct (px, py) = p
                                let ddx = px - centerX
                                let ddy = py - centerY

                                if ddx * ddx + ddy * ddy <= radius * radius then
                                    results.Add id
                            | false, _ -> ()

            results.ToArray() |> Array.sort |> Set.ofArray

        let model () =
            let struct (centerX, centerY) = center

            seq {
                for id in modelLive do
                    match modelPos.TryGetValue id with
                    | true, p ->
                        let struct (px, py) = p
                        let ddx = px - centerX
                        let ddy = py - centerY

                        if ddx * ddx + ddy * ddy <= radius * radius then
                            id
                    | false, _ -> ()
            }
            |> Set.ofSeq

        let apply (op: int) =
            let kind = op % 3
            let rest = op / 3
            let id = rest % 20
            let x = float ((rest / 20) % 20)
            let y = float ((rest / 400) % 20)

            match kind with
            | 0 -> // Upsert the position and mark the entity live.
                CMap.addOrUpdate id (struct (x, y)) positions
                modelPos[id] <- struct (x, y)
                CSet.add id live
                modelLive.Add id |> ignore
            | 1 -> // Remove the entity.
                CMap.remove id positions
                modelPos.Remove id |> ignore
                CSet.remove id live
                modelLive.Remove id |> ignore
            | _ -> // Toggle liveness only.
                CSet.add id live
                modelLive.Add id |> ignore

        let mutable ok = true

        for op in ops do
            apply op

            if query () <> model () then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``AList choose matches the combat-status model`` () =
    let prop (ops: int list) =
        let effects = CList.empty<int> // effect kind

        // Kipo: stun/silence map to a status, everything else is dropped.
        let effectKindToStatus (kind: int) =
            if kind % 4 = 0 then Some(kind * 10) else None

        let statuses = effects |> AList.choose effectKindToStatus
        let model = ResizeArray<int>()

        let apply (op: int) =
            let kind = op % 3
            let rest = op / 3
            let element = rest % 10

            let position =
                // F#'s % is signed: normalize to [0, Count + 1).
                let p = (rest / 10) % (model.Count + 1)
                if p < 0 then p + (model.Count + 1) else p

            match kind with
            | 0 -> // Insert an effect at the position.
                CList.insertAt position element effects
                model.Insert(position, element)
            | 1 -> // Remove the effect at the position.
                if model.Count > 0 && position < model.Count then
                    CList.removeAt position effects
                    model.RemoveAt position
            | _ -> // Update the effect at the position.
                if model.Count > 0 && position < model.Count then
                    CList.updateAt position element effects
                    model[position] <- element

        let mutable ok = true

        for op in ops do
            apply op

            let actual = AList.toArray statuses
            let expected = Array.ofSeq (Seq.choose effectKindToStatus model)

            if actual <> expected then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop
