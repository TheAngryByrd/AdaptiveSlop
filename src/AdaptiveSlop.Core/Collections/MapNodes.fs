namespace AdaptiveSlop.Core

open System
open System.Buffers
open System.Collections.Generic

// =============================================================================
// Constant adaptive collections
// =============================================================================

type ConstantMap<'K, 'V when 'K: comparison>(value: Map<'K, 'V>) =
    interface IAdaptiveMap<'K, 'V> with
        member this.GetValue() =
            AdaptiveRuntime.addDependency (this :> IAdaptiveObject) 0L
            value

        member _.Version = 0L

// =============================================================================
// AdaptiveMap transform nodes
// =============================================================================

type MapMapNode<'K, 'V, 'U when 'K: comparison>
    (source: IAdaptiveMap<'K, 'V>, [<InlineIfLambda>] mapping: 'K -> 'V -> 'U) as this =

    let mutable version = 0L
    let data = Dictionary<'K, 'U>()
    let mutable snapshot = Map.empty<'K, 'U>
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

            for (KeyValue(k, v)) in sourceItems do
                data[k] <- mapping k v

            snapshot <- Map.ofSeq (data |> Seq.map (fun (KeyValue(k, v)) -> (k, v)))
            snapshotVersion <- 0L

    member private this.Register() =
        if not registered then
            registered <- true

            match box source with
            | :? IMapSinkRegistry as r -> r.AddMapSink(box (this :> IMapDeltaSink<'K, 'V>))
            | _ -> ()

    member private this.Unregister() =
        if registered then
            registered <- false

            match box source with
            | :? IMapSinkRegistry as r -> r.RemoveMapSink(box (this :> IMapDeltaSink<'K, 'V>))
            | _ -> ()

    member internal this.AddSink(sink: IMapDeltaSink<'K, 'U>) =
        if sinkCount = sinks.Length then
            let next = Array.zeroCreate (sinks.Length * 2)
            Array.Copy(sinks, next, sinks.Length)
            sinks <- next

        sinks[sinkCount] <- box sink
        sinkCount <- sinkCount + 1

        if sinkCount = 1 then
            this.Register()

    member internal this.RemoveSink(sink: IMapDeltaSink<'K, 'U>) =
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

    member private this.FlushDeltas(ver: int64, sets: struct ('K * 'U)[], setCnt: int, rems: 'K[], remCnt: int) =
        if setCnt > 0 || remCnt > 0 then
            let sinksSnapshot =
                if sinkCount = 0 then
                    [||]
                else
                    let arr = Array.zeroCreate sinkCount
                    Array.Copy(sinks, arr, sinkCount)
                    arr

            for i in 0 .. sinksSnapshot.Length - 1 do
                (unbox<IMapDeltaSink<'K, 'U>> sinksSnapshot[i]).OnDeltas(ver, sets, setCnt, rems, remCnt)

    interface IMapDeltaSink<'K, 'V> with
        member this.OnDeltas(ver, sets, setCnt, rems, remCnt) =
            let ownSets =
                if setCnt > 0 then
                    ArrayPool<struct ('K * 'U)>.Shared.Rent setCnt
                else
                    ArrayPool<struct ('K * 'U)>.Shared.Rent 1

            let ownRems =
                if remCnt > 0 then
                    ArrayPool<'K>.Shared.Rent remCnt
                else
                    ArrayPool<'K>.Shared.Rent 1

            let mutable ownSetCnt = 0
            let mutable ownRemCnt = 0

            for i in 0 .. remCnt - 1 do
                let k = rems[i]

                if data.Remove k then
                    snapshot <- Map.remove k snapshot
                    ownRems[ownRemCnt] <- k
                    ownRemCnt <- ownRemCnt + 1

            for i in 0 .. setCnt - 1 do
                let struct (k, v) = sets[i]
                let u = mapping k v
                data[k] <- u
                snapshot <- Map.add k u snapshot
                ownSets[ownSetCnt] <- struct (k, u)
                ownSetCnt <- ownSetCnt + 1

            version <- ver
            snapshotVersion <- ver

            this.FlushDeltas(ver, ownSets, ownSetCnt, ownRems, ownRemCnt)
            ArrayPool<struct ('K * 'U)>.Shared.Return(ownSets, true)
            ArrayPool<'K>.Shared.Return(ownRems, true)

    interface IAdaptiveMap<'K, 'U> with
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

    interface IMapSinkRegistry with
        member this.AddMapSink(sink) =
            this.AddSink(unbox<IMapDeltaSink<'K, 'U>> sink)

        member this.RemoveMapSink(sink) =
            this.RemoveSink(unbox<IMapDeltaSink<'K, 'U>> sink)


type FilterMapNode<'K, 'V when 'K: comparison>
    (source: IAdaptiveMap<'K, 'V>, [<InlineIfLambda>] predicate: 'K -> 'V -> bool) as this =

    let mutable version = 0L
    let data = Dictionary<'K, 'V>()
    let mutable snapshot = Map.empty<'K, 'V>
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

            for (KeyValue(k, v)) in sourceItems do
                if predicate k v then
                    data[k] <- v

            snapshot <- Map.ofSeq (data |> Seq.map (fun (KeyValue(k, v)) -> (k, v)))
            snapshotVersion <- 0L

    member private this.Register() =
        if not registered then
            registered <- true

            match source with
            | :? ChangeableMap<'K, 'V> as cm -> cm.AddSink(this :> IMapDeltaSink<'K, 'V>)
            | :? MapMapNode<'K, 'V, 'V> as mn -> mn.AddSink(this :> IMapDeltaSink<'K, 'V>)
            | :? FilterMapNode<'K, 'V> as fn -> fn.AddSink(this :> IMapDeltaSink<'K, 'V>)
            | _ -> ()

    member private this.Unregister() =
        if registered then
            registered <- false

            match source with
            | :? ChangeableMap<'K, 'V> as cm -> cm.RemoveSink(this :> IMapDeltaSink<'K, 'V>)
            | :? MapMapNode<'K, 'V, 'V> as mn -> mn.RemoveSink(this :> IMapDeltaSink<'K, 'V>)
            | :? FilterMapNode<'K, 'V> as fn -> fn.RemoveSink(this :> IMapDeltaSink<'K, 'V>)
            | _ -> ()

    member internal this.AddSink(sink: IMapDeltaSink<'K, 'V>) =
        if sinkCount = sinks.Length then
            let next = Array.zeroCreate (sinks.Length * 2)
            Array.Copy(sinks, next, sinks.Length)
            sinks <- next

        sinks[sinkCount] <- box sink
        sinkCount <- sinkCount + 1

        if sinkCount = 1 then
            this.Register()

    member internal this.RemoveSink(sink: IMapDeltaSink<'K, 'V>) =
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

    member private this.FlushDeltas(ver: int64, sets: struct ('K * 'V)[], setCnt: int, rems: 'K[], remCnt: int) =
        if setCnt > 0 || remCnt > 0 then
            let sinksSnapshot =
                if sinkCount = 0 then
                    [||]
                else
                    let arr = Array.zeroCreate sinkCount
                    Array.Copy(sinks, arr, sinkCount)
                    arr

            for i in 0 .. sinksSnapshot.Length - 1 do
                (unbox<IMapDeltaSink<'K, 'V>> sinksSnapshot[i]).OnDeltas(ver, sets, setCnt, rems, remCnt)

    interface IMapDeltaSink<'K, 'V> with
        member this.OnDeltas(ver, sets, setCnt, rems, remCnt) =
            let ownSets =
                if setCnt > 0 then
                    ArrayPool<struct ('K * 'V)>.Shared.Rent setCnt
                else
                    ArrayPool<struct ('K * 'V)>.Shared.Rent 1

            let ownRems =
                if remCnt > 0 then
                    ArrayPool<'K>.Shared.Rent remCnt
                else
                    ArrayPool<'K>.Shared.Rent 1

            let mutable ownSetCnt = 0
            let mutable ownRemCnt = 0

            for i in 0 .. remCnt - 1 do
                let k = rems[i]

                if data.Remove k then
                    snapshot <- Map.remove k snapshot
                    ownRems[ownRemCnt] <- k
                    ownRemCnt <- ownRemCnt + 1

            for i in 0 .. setCnt - 1 do
                let struct (k, v) = sets[i]

                if predicate k v then
                    data[k] <- v
                    snapshot <- Map.add k v snapshot
                    ownSets[ownSetCnt] <- struct (k, v)
                    ownSetCnt <- ownSetCnt + 1
                elif data.Remove k then
                    snapshot <- Map.remove k snapshot
                    ownRems[ownRemCnt] <- k
                    ownRemCnt <- ownRemCnt + 1

            version <- ver
            snapshotVersion <- ver

            this.FlushDeltas(ver, ownSets, ownSetCnt, ownRems, ownRemCnt)
            ArrayPool<struct ('K * 'V)>.Shared.Return(ownSets, true)
            ArrayPool<'K>.Shared.Return(ownRems, true)

    interface IAdaptiveMap<'K, 'V> with
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
