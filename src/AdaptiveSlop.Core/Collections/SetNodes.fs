namespace AdaptiveSlop.Core

open System
open System.Buffers
open System.Collections.Generic

// =============================================================================
// Constant adaptive collections
// =============================================================================

type ConstantSet<'T when 'T: comparison>(value: Set<'T>) =
    interface IAdaptiveSet<'T> with
        member this.GetValue() =
            AdaptiveRuntime.addDependency (this :> IAdaptiveObject) 0L
            value

        member _.Version = 0L

// =============================================================================
// AdaptiveSet transform nodes
// =============================================================================

type MapSetNode<'T, 'U when 'T: comparison and 'U: comparison>
    (source: IAdaptiveSet<'T>, [<InlineIfLambda>] mapping: 'T -> 'U) as this =

    let mutable version = 0L
    let data = HashSet<'U>()
    let refcounts = Dictionary<'U, int>()
    let mutable snapshot = Set.empty<'U>
    let mutable snapshotVersion = -1L
    let mutable sinks: obj[] = Array.zeroCreate 4
    let mutable sinkCount = 0
    let mutable registered = false
    let mutable initialized = false

    do this.Register()

    member private this.DoInitialLoad() =
        if not initialized then
            initialized <- true
            let sourceItems = source.GetValue()

            for item in sourceItems do
                let y = mapping item
                data.Add y |> ignore

                match refcounts.TryGetValue y with
                | true, n -> refcounts[y] <- n + 1
                | _ -> refcounts[y] <- 1

            snapshot <- Set.ofSeq data
            snapshotVersion <- 0L

    member private this.Register() =
        if not registered then
            registered <- true

            match box source with
            | :? ISetSinkRegistry as r -> r.AddSetSink(box (this :> ISetDeltaSink<'T>))
            | _ -> ()

    member private this.Unregister() =
        if registered then
            registered <- false

            match box source with
            | :? ISetSinkRegistry as r -> r.RemoveSetSink(box (this :> ISetDeltaSink<'T>))
            | _ -> ()

    member internal this.AddSink(sink: ISetDeltaSink<'U>) =
        if sinkCount = sinks.Length then
            let next = Array.zeroCreate (sinks.Length * 2)
            Array.Copy(sinks, next, sinks.Length)
            sinks <- next

        sinks[sinkCount] <- box sink
        sinkCount <- sinkCount + 1

        if sinkCount = 1 then
            this.Register()

    member internal this.RemoveSink(sink: ISetDeltaSink<'U>) =
        let mutable becameZero = false
        let mutable found = -1
        let mutable i = 0

        while found < 0 && i < sinkCount do
            if obj.ReferenceEquals(sinks[i], box sink) then
                found <- i
            else
                i <- i + 1

        if found >= 0 then
            sinkCount <- sinkCount - 1

            for j in found .. sinkCount - 1 do
                sinks[j] <- sinks[j + 1]

            sinks[sinkCount] <- null
            becameZero <- sinkCount = 0

        if becameZero then
            this.Unregister()

    member private this.FlushDeltas(ver: int64, adds: 'U[], addCnt: int, rems: 'U[], remCnt: int) =
        if addCnt > 0 || remCnt > 0 then
            let sinksSnapshot =
                if sinkCount = 0 then
                    [||]
                else
                    let arr = Array.zeroCreate sinkCount
                    Array.Copy(sinks, arr, sinkCount)
                    arr

            for i in 0 .. sinksSnapshot.Length - 1 do
                (unbox<ISetDeltaSink<'U>> sinksSnapshot[i]).OnDeltas(ver, adds, addCnt, rems, remCnt)

    interface ISetDeltaSink<'T> with
        member this.OnDeltas(ver, adds, addCnt, rems, remCnt) =
            let ownAdds =
                if addCnt > 0 then
                    ArrayPool<'U>.Shared.Rent addCnt
                else
                    ArrayPool<'U>.Shared.Rent 1

            let ownRems =
                if remCnt > 0 then
                    ArrayPool<'U>.Shared.Rent remCnt
                else
                    ArrayPool<'U>.Shared.Rent 1

            let mutable ownAddCnt = 0
            let mutable ownRemCnt = 0

            for i in 0 .. remCnt - 1 do
                let y = mapping rems[i]

                match refcounts.TryGetValue y with
                | true, 1 ->
                    refcounts.Remove y |> ignore
                    data.Remove y |> ignore
                    snapshot <- Set.remove y snapshot
                    ownRems[ownRemCnt] <- y
                    ownRemCnt <- ownRemCnt + 1
                | true, n -> refcounts[y] <- n - 1
                | _ -> ()

            for i in 0 .. addCnt - 1 do
                let y = mapping adds[i]

                match refcounts.TryGetValue y with
                | true, n ->
                    refcounts[y] <- n + 1
                    ownAdds[ownAddCnt] <- y
                    ownAddCnt <- ownAddCnt + 1
                | _ ->
                    refcounts[y] <- 1
                    data.Add y |> ignore
                    snapshot <- Set.add y snapshot
                    ownAdds[ownAddCnt] <- y
                    ownAddCnt <- ownAddCnt + 1

            version <- ver
            snapshotVersion <- ver

            this.FlushDeltas(ver, ownAdds, ownAddCnt, ownRems, ownRemCnt)
            ArrayPool<'U>.Shared.Return(ownAdds, true)
            ArrayPool<'U>.Shared.Return(ownRems, true)

    interface IAdaptiveSet<'U> with
        member this.GetValue() =
            let ctx = GraphContext.Default
            ctx.ClaimOwner()

            try
                this.DoInitialLoad()
                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) version
                snapshot
            finally
                ctx.ReleaseOwner()

        member _.Version = version

    interface ISetSinkRegistry with
        member this.AddSetSink(sink) =
            this.AddSink(unbox<ISetDeltaSink<'U>> sink)

        member this.RemoveSetSink(sink) =
            this.RemoveSink(unbox<ISetDeltaSink<'U>> sink)


type FilterSetNode<'T when 'T: comparison>(source: IAdaptiveSet<'T>, [<InlineIfLambda>] predicate: 'T -> bool) as this =

    let mutable version = 0L
    let data = HashSet<'T>()
    let refcounts = Dictionary<'T, int>()
    let mutable snapshot = Set.empty<'T>
    let mutable snapshotVersion = -1L
    let mutable sinks: obj[] = Array.zeroCreate 4
    let mutable sinkCount = 0
    let mutable registered = false
    let mutable initialized = false

    do this.Register()

    member private this.DoInitialLoad() =
        if not initialized then
            initialized <- true
            let sourceItems = source.GetValue()

            for item in sourceItems do
                if predicate item then
                    data.Add item |> ignore

                    match refcounts.TryGetValue item with
                    | true, n -> refcounts[item] <- n + 1
                    | _ -> refcounts[item] <- 1

            snapshot <- Set.ofSeq data
            snapshotVersion <- 0L

    member private this.Register() =
        if not registered then
            registered <- true

            match box source with
            | :? ISetSinkRegistry as r -> r.AddSetSink(box (this :> ISetDeltaSink<'T>))
            | _ -> ()

    member private this.Unregister() =
        if registered then
            registered <- false

            match box source with
            | :? ISetSinkRegistry as r -> r.RemoveSetSink(box (this :> ISetDeltaSink<'T>))
            | _ -> ()

    member internal this.AddSink(sink: ISetDeltaSink<'T>) =
        if sinkCount = sinks.Length then
            let next = Array.zeroCreate (sinks.Length * 2)
            Array.Copy(sinks, next, sinks.Length)
            sinks <- next

        sinks[sinkCount] <- box sink
        sinkCount <- sinkCount + 1

        if sinkCount = 1 then
            this.Register()

    member internal this.RemoveSink(sink: ISetDeltaSink<'T>) =
        let mutable becameZero = false
        let mutable found = -1
        let mutable i = 0

        while found < 0 && i < sinkCount do
            if obj.ReferenceEquals(sinks[i], box sink) then
                found <- i
            else
                i <- i + 1

        if found >= 0 then
            sinkCount <- sinkCount - 1

            for j in found .. sinkCount - 1 do
                sinks[j] <- sinks[j + 1]

            sinks[sinkCount] <- null
            becameZero <- sinkCount = 0

        if becameZero then
            this.Unregister()

    member private this.FlushDeltas(ver: int64, adds: 'T[], addCnt: int, rems: 'T[], remCnt: int) =
        if addCnt > 0 || remCnt > 0 then
            let sinksSnapshot =
                if sinkCount = 0 then
                    [||]
                else
                    let arr = Array.zeroCreate sinkCount
                    Array.Copy(sinks, arr, sinkCount)
                    arr

            for i in 0 .. sinksSnapshot.Length - 1 do
                (unbox<ISetDeltaSink<'T>> sinksSnapshot[i]).OnDeltas(ver, adds, addCnt, rems, remCnt)

    interface ISetDeltaSink<'T> with
        member this.OnDeltas(ver, adds, addCnt, rems, remCnt) =
            let ownAdds =
                if addCnt > 0 then
                    ArrayPool<'T>.Shared.Rent addCnt
                else
                    ArrayPool<'T>.Shared.Rent 1

            let ownRems =
                if remCnt > 0 then
                    ArrayPool<'T>.Shared.Rent remCnt
                else
                    ArrayPool<'T>.Shared.Rent 1

            let mutable ownAddCnt = 0
            let mutable ownRemCnt = 0

            for i in 0 .. remCnt - 1 do
                let x = rems[i]

                match refcounts.TryGetValue x with
                | true, 1 ->
                    refcounts.Remove x |> ignore
                    data.Remove x |> ignore
                    snapshot <- Set.remove x snapshot
                    ownRems[ownRemCnt] <- x
                    ownRemCnt <- ownRemCnt + 1
                | true, n -> refcounts[x] <- n - 1
                | _ -> ()

            for i in 0 .. addCnt - 1 do
                let x = adds[i]

                if predicate x then
                    match refcounts.TryGetValue x with
                    | true, n ->
                        refcounts[x] <- n + 1
                        ownAdds[ownAddCnt] <- x
                        ownAddCnt <- ownAddCnt + 1
                    | _ ->
                        refcounts[x] <- 1
                        data.Add x |> ignore
                        snapshot <- Set.add x snapshot
                        ownAdds[ownAddCnt] <- x
                        ownAddCnt <- ownAddCnt + 1

            version <- ver
            snapshotVersion <- ver

            this.FlushDeltas(ver, ownAdds, ownAddCnt, ownRems, ownRemCnt)
            ArrayPool<'T>.Shared.Return(ownAdds, true)
            ArrayPool<'T>.Shared.Return(ownRems, true)

    interface IAdaptiveSet<'T> with
        member this.GetValue() =
            let ctx = GraphContext.Default
            ctx.ClaimOwner()

            try
                this.DoInitialLoad()
                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) version
                snapshot
            finally
                ctx.ReleaseOwner()

        member _.Version = version

    interface ISetSinkRegistry with
        member this.AddSetSink(sink) =
            this.AddSink(unbox<ISetDeltaSink<'T>> sink)

        member this.RemoveSetSink(sink) =
            this.RemoveSink(unbox<ISetDeltaSink<'T>> sink)


type UnionSetNode<'T when 'T: comparison>(left: IAdaptiveSet<'T>, right: IAdaptiveSet<'T>) as this =

    let mutable version = 0L
    let data = HashSet<'T>()
    let refcounts = Dictionary<'T, int>()
    let mutable snapshot = Set.empty<'T>
    let mutable snapshotVersion = -1L
    let mutable sinks: obj[] = Array.zeroCreate 4
    let mutable sinkCount = 0
    let mutable regLeft = false
    let mutable regRight = false
    let mutable initialized = false

    do
        regLeft <- true
        regRight <- true
        this.RegisterSide left
        this.RegisterSide right

    member private this.DoInitialLoad() =
        if not initialized then
            initialized <- true

            let addItem (x: 'T) =
                data.Add x |> ignore

                match refcounts.TryGetValue x with
                | true, n -> refcounts[x] <- n + 1
                | _ -> refcounts[x] <- 1

            let leftItems = left.GetValue()

            for item in leftItems do
                addItem item

            let rightItems = right.GetValue()

            for item in rightItems do
                addItem item

            snapshot <- Set.ofSeq data
            snapshotVersion <- 0L

    member private this.RegisterSide(s: IAdaptiveSet<'T>) =
        match box s with
        | :? ISetSinkRegistry as r -> r.AddSetSink(box (this :> ISetDeltaSink<'T>))
        | _ -> ()

    member private this.UnregisterSide(s: IAdaptiveSet<'T>) =
        match box s with
        | :? ISetSinkRegistry as r -> r.RemoveSetSink(box (this :> ISetDeltaSink<'T>))
        | _ -> ()

    member internal this.AddSink(sink: ISetDeltaSink<'T>) =
        if sinkCount = sinks.Length then
            let next = Array.zeroCreate (sinks.Length * 2)
            Array.Copy(sinks, next, sinks.Length)
            sinks <- next

        sinks[sinkCount] <- box sink
        sinkCount <- sinkCount + 1

        if sinkCount = 1 then
            regLeft <- true
            regRight <- true

        if sinkCount = 1 then
            this.RegisterSide left
            this.RegisterSide right

    member internal this.RemoveSink(sink: ISetDeltaSink<'T>) =
        let mutable becameZero = false
        let mutable found = -1
        let mutable i = 0

        while found < 0 && i < sinkCount do
            if obj.ReferenceEquals(sinks[i], box sink) then
                found <- i
            else
                i <- i + 1

        if found >= 0 then
            sinkCount <- sinkCount - 1

            for j in found .. sinkCount - 1 do
                sinks[j] <- sinks[j + 1]

            sinks[sinkCount] <- null
            becameZero <- sinkCount = 0

        if becameZero then
            this.UnregisterSide left
            this.UnregisterSide right

    member private this.FlushDeltas(ver: int64, adds: 'T[], addCnt: int, rems: 'T[], remCnt: int) =
        if addCnt > 0 || remCnt > 0 then
            let sinksSnapshot =
                if sinkCount = 0 then
                    [||]
                else
                    let arr = Array.zeroCreate sinkCount
                    Array.Copy(sinks, arr, sinkCount)
                    arr

            for i in 0 .. sinksSnapshot.Length - 1 do
                (unbox<ISetDeltaSink<'T>> sinksSnapshot[i]).OnDeltas(ver, adds, addCnt, rems, remCnt)

    member private this.ProcessDeltas(adds: 'T[], addCnt: int, rems: 'T[], remCnt: int) =
        if addCnt = 0 && remCnt = 0 then
            ()
        else

            let ownAdds =
                if addCnt > 0 then
                    ArrayPool<'T>.Shared.Rent addCnt
                else
                    ArrayPool<'T>.Shared.Rent 1

            let ownRems =
                if remCnt > 0 then
                    ArrayPool<'T>.Shared.Rent remCnt
                else
                    ArrayPool<'T>.Shared.Rent 1

            let mutable ownAddCnt = 0
            let mutable ownRemCnt = 0

            for i in 0 .. remCnt - 1 do
                let x = rems[i]

                match refcounts.TryGetValue x with
                | true, 1 ->
                    refcounts.Remove x |> ignore
                    data.Remove x |> ignore
                    snapshot <- Set.remove x snapshot
                    ownRems[ownRemCnt] <- x
                    ownRemCnt <- ownRemCnt + 1
                | true, n -> refcounts[x] <- n - 1
                | _ -> ()

            for i in 0 .. addCnt - 1 do
                let x = adds[i]

                match refcounts.TryGetValue x with
                | true, n -> refcounts[x] <- n + 1
                | _ ->
                    refcounts[x] <- 1
                    data.Add x |> ignore
                    snapshot <- Set.add x snapshot
                    ownAdds[ownAddCnt] <- x
                    ownAddCnt <- ownAddCnt + 1

            snapshotVersion <- version

            if ownAddCnt > 0 || ownRemCnt > 0 then
                this.FlushDeltas(version, ownAdds, ownAddCnt, ownRems, ownRemCnt)

            ArrayPool<'T>.Shared.Return(ownAdds, true)
            ArrayPool<'T>.Shared.Return(ownRems, true)

    interface ISetDeltaSink<'T> with
        member this.OnDeltas(ver, adds, addCnt, rems, remCnt) =
            version <- ver
            this.ProcessDeltas(adds, addCnt, rems, remCnt)

    member this.OnRightDeltas(ver, adds, addCnt, rems, remCnt) =
        version <- ver
        this.ProcessDeltas(adds, addCnt, rems, remCnt)

    interface IAdaptiveSet<'T> with
        member this.GetValue() =
            let ctx = GraphContext.Default
            ctx.ClaimOwner()

            try
                this.DoInitialLoad()
                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) version
                snapshot
            finally
                ctx.ReleaseOwner()

        member _.Version = version

    interface ISetSinkRegistry with
        member this.AddSetSink(sink) =
            this.AddSink(unbox<ISetDeltaSink<'T>> sink)

        member this.RemoveSetSink(sink) =
            this.RemoveSink(unbox<ISetDeltaSink<'T>> sink)
