﻿namespace Nessos.MBrace.Runtime.Store

    open System
    open System.IO
    open System.Reflection
    open System.Runtime.Serialization

    open Nessos.MBrace
    open Nessos.MBrace.Core
    open Nessos.MBrace.Utils
    open Nessos.MBrace.Runtime


    type MutableCloudRef<'T> internal (id : string, container : string, tag : Tag, provider : MutableCloudRefProvider) =

        member val internal Tag = tag with get, set

        member __.Name = id
        member __.Container = container

        interface IMutableCloudRef<'T> with
            member self.Name = id
            member self.Container = container
            member self.Type = typeof<'T>

            member self.ReadValue () = provider.Dereference self
            member self.ReadValue () = async { let! t = provider.Dereference self in return t :> obj }

            member self.TryUpdate (value : 'T) = provider.TryUpdate(self, value)
            member self.TryUpdate (value : obj) = async {
                match value with
                | :? 'T as t -> return! provider.TryUpdate(self, t)
                | _ -> return invalidArg "value" <| sprintf "update value not of type '%O'" typeof<'T>
            }

            member self.ForceUpdate (value : 'T) = provider.ForceUpdate(self, value)
            member self.ForceUpdate (value : obj) = async {
                match value with
                | :? 'T as t -> return! provider.ForceUpdate(self, t)
                | _ -> return invalidArg "value" <| sprintf "update value not of type '%O'" typeof<'T>
            }

            member self.Dispose () = provider.Delete self
            member self.Value = provider.Dereference self |> Async.RunSynchronously

        new (info : SerializationInfo, context : StreamingContext) = 
            let id          = info.GetString("id")
            let container   = info.GetString("container")
            let tag         = info.GetString("tag")
            let storeId     = info.GetValue("storeId", typeof<StoreId>) :?> StoreId
            let config =
                match StoreRegistry.TryGetCoreConfiguration storeId with
                | None -> raise <| new StoreException(sprintf "No configuration for store '%s' has been activated." storeId.AssemblyQualifiedName)
                | Some config -> config.MutableCloudRefProvider :?> MutableCloudRefProvider

            new MutableCloudRef<'T>(id, container, tag, config)
        
        interface ISerializable with 
            member self.GetObjectData(info : SerializationInfo, _ : StreamingContext) =
                info.AddValue("id", id)
                info.AddValue("container", container)
                info.AddValue("tag", tag)
                info.AddValue("storeId", provider.StoreId, typeof<StoreId>)


    and internal MutableCloudRefProvider(storeInfo : StoreInfo) as self =

        static let extension = "mref"
        static let postfix s = sprintf' "%s.%s" s extension

        static let serialize (value : obj) (ty : Type) (stream : Stream) = async {
            Serialization.DefaultPickler.Serialize<Type>(stream, ty)
            Serialization.DefaultPickler.Serialize<obj>(stream, value)
        }

        let read container id : Async<Type * obj * Tag> = async {
            let id = postfix id
            let! stream, tag = storeInfo.Store.ReadMutable(container, id)
            let t = Serialization.DefaultPickler.Deserialize<Type> stream
            let o = Serialization.DefaultPickler.Deserialize<obj> stream
            stream.Dispose()
            return t, o, tag
        }

        let readInfo container id = async {
            let! stream, tag = storeInfo.Store.ReadMutable(container, id)        
            let t = Serialization.DefaultPickler.Deserialize<Type> stream
            stream.Dispose()
            return t, tag
        }

        let getIds (container : string) : Async<string []> = async {
            let! files = storeInfo.Store.GetAllFiles(container)
            return files 
                    |> Seq.filter (fun w -> w.EndsWith extension)
                    |> Seq.map (fun w -> w.Substring(0, w.Length - extension.Length - 1))
                    |> Seq.toArray
        }

        let defineUntyped(ty : Type, container : string, id : string, tag : string) =
            typeof<MutableCloudRefProvider>
                .GetMethod("CreateCloudRef", BindingFlags.Static ||| BindingFlags.NonPublic)
                .MakeGenericMethod([| ty |])
                .Invoke(null, [| id :> obj ; container :> obj ; tag :> obj ; self :> obj |])
                :?> IMutableCloudRef

        // WARNING : method called by reflection from 'defineUntyped' function above
        static member CreateCloudRef<'T>(id, container, tag, provider) =
            new MutableCloudRef<'T>(id , container, tag, provider)

        member self.StoreId = storeInfo.Id

        member self.Delete(mref : MutableCloudRef<'T>) : Async<unit> = 
            storeInfo.Store.Delete(mref.Container, postfix mref.Name)

        member self.Dereference(mref : MutableCloudRef<'T>) : Async<'T> =
            async {
                let! _, value, tag = read mref.Container mref.Name
                mref.Tag <- tag
                return value :?> 'T
            }

        member this.TryUpdate(mref : MutableCloudRef<'T>, value : 'T) : Async<bool>  =
            async {
                let! ok, tag = storeInfo.Store.TryUpdateMutable(mref.Container, postfix mref.Name, serialize value typeof<'T>, mref.Tag)

                if ok then mref.Tag <- tag
                return ok
            }

        member this.ForceUpdate(mref : MutableCloudRef<'T>, value : 'T) : Async<unit> =
            async {
                let! tag = storeInfo.Store.ForceUpdateMutable(mref.Container, postfix mref.Name, serialize value typeof<'T>)
                mref.Tag <- tag
            }

        interface IMutableCloudRefProvider with

            member self.Create (container : string, id : string, value : 'T) : Async<IMutableCloudRef<'T>> = 
                async {
                    let! tag = storeInfo.Store.CreateMutable(container, postfix id, serialize value typeof<'T>)

                    return new MutableCloudRef<'T>(id, container, tag, self) :> _
                }

            member self.Create (container : string, id : string, t : Type, value : obj) : Async<IMutableCloudRef> = 
                async {
                    let! tag = storeInfo.Store.CreateMutable(container, postfix id, serialize value t)

                    return defineUntyped(t, container, id, tag)
                }

            member self.GetExisting (container , id) : Async<IMutableCloudRef> =
                async {
                    let! t, tag = readInfo container (postfix id)
                    return defineUntyped(t, container, id, tag)
                }

            member self.GetContainedRefs(container : string) : Async<IMutableCloudRef []> =
                async {
                    let! ids = getIds container
                    return! 
                        ids 
                        |> Array.map (fun id -> (self :> IMutableCloudRefProvider).GetExisting(container, id))
                        |> Async.Parallel
                }