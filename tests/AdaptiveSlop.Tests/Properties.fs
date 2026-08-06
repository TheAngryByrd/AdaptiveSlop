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
