﻿namespace Nessos.MBrace.Client

    open System.Reflection

    open Microsoft.FSharp.Quotations

    open Nessos.Vagrant

    open Nessos.MBrace
    open Nessos.MBrace.Runtime
    open Nessos.MBrace.Runtime.MBraceException

    open Nessos.MBrace.Utils
    open Nessos.MBrace.Utils.Quotations

    type CloudComputation =
        
        static member Compile(expr : Expr<Cloud<'T>>, ?name) =
            // force Vagrant compilation first
            let dependencies = MBraceSettings.Vagrant.ComputeObjectDependencies(expr, permitCompilation = true)

            // build cloud computation package
            let comp = CloudComputationPackage.Compile(expr, ?name = name)

            // check for errors
            match comp.Errors with
            | [] -> new CloudComputation<'T>(comp, dependencies, comp.Warnings)
            | errors ->
                let errors = String.concat "\n" errors
                mfailwithf "Supplied cloud block contains errors:\n%s" errors

    and CloudComputation<'T> internal (comp : CloudComputationPackage, dependencies : Assembly list, warnings : string list) =

        member __.Name = comp.Name
        member __.Expr = comp.Expr
        member __.Warnings = warnings
        member __.Dependencies = dependencies

        member internal __.Image =
            {
                ClientId = MBraceSettings.ClientId
                Name = comp.Name
                Computation = Serialization.Serialize comp
                Type = Serialization.Serialize comp.ReturnType
                TypeName = Reflection.prettyPrint typeof<'T>
                Dependencies = dependencies |> List.map VagrantUtils.ComputeAssemblyId
            }