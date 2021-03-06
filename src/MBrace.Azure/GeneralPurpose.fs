﻿namespace Nessos.MBrace.Azure

    open System
    open System.IO
    open Nessos.MBrace.Azure.Common
    open Microsoft.WindowsAzure.Storage
    open Microsoft.WindowsAzure.Storage.Blob
    open Microsoft.WindowsAzure.Storage.Table

    type internal GeneralPurpose (account : CloudStorageAccount) =

        let blobClient () = Clients.getBlobClient account
        let tableClient () = Clients.getTableClient account
        let getTable name = tableClient().GetTableReference(name)
        let immblob  = ImmutableBlobStoreProvider (account)
        let immtable = ImmutableTableStoreProvider(account)

        let getReadBlob (folder, file)  =
            let container = (blobClient()).GetContainerReference(folder)

            if container.Exists() |> not 
            then failwith "Trying to read from non-existent container"
            
            let blob = container.GetBlockBlobReference(file)

            if blob.Exists() |> not
            then failwith "Trying to read from non-existent blob"

            blob

        let getWriteBlob(folder, file) =
            let container = (blobClient()).GetContainerReference(folder)
            container.CreateIfNotExists() |> ignore
                
            let blob = container.GetBlockBlobReference(file)
            blob

        let readEntity (folder, file) =
            let table = getTable folder
            let retrieveOp = TableOperation.Retrieve<MutableFatEntity>(file, String.Empty)
            let result = (getTable folder).Execute(retrieveOp)
            result.Result, result.Etag  
        
        member this.Exists(folder, file) =
            async {
                let! b1 = immtable.Exists(folder, file)
                if b1 then return true 
                else return! immblob.Exists(folder, file)   
            }

        member this.Exists(folder) =
            async {
                let! b1 = immtable.Exists(folder)
                if b1 then return true 
                else return! immblob.Exists(folder)
            }

        member this.GetFiles(folder) =
            async {
                let! a1 = immtable.GetFiles(folder)
                let! a2 = immblob.GetFiles(folder)
                return Array.append a1 a2
                       |> (Set.ofSeq >> Array.ofSeq)
            }

        member this.GetFolders () =
            let containers = blobClient().ListContainers() |> Seq.map (fun s -> s.Name)
            //ListContainers(Helpers.containerPrefix, ContainerListingDetails.Metadata) 
            let tables = tableClient().ListTables() |> Seq.map (fun s -> s.Name)
            Seq.append tables containers
            |> (Set.ofSeq >> Array.ofSeq)
            |> async.Return

        member this.Delete(folder) =
            async {   
                let! b1 = immtable.Exists(folder)
                if b1 then 
                    do! immtable.Delete(folder)
                let! b2 = immblob.Exists(folder) 
                if b2 then 
                    do! immblob.Delete(folder)
                if not b1 && not b2 then 
                    raise <| ArgumentException(sprintf "Non-existent %s" folder)
            }

        member this.Delete(folder, file) =
            async {
                let! b1 = immtable.Exists(folder, file)
                if b1 then 
                    do! immtable.Delete(folder, file)
                let! b2 = immblob.Exists(folder, file) 
                if b2 then 
                    do! immblob.Delete(folder, file)
                if not b1 && not b2 then
                    raise <| ArgumentException(sprintf "Non-existent %s - %s" folder file)
            }