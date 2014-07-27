﻿namespace Nessos.FsPickler.Tests

    open PerfUtil
    open PerfUtil.NUnit

    open NUnit.Framework

    open Nessos.FsPickler
    open Nessos.FsPickler.Json

    [<AbstractClass>]
    type PerfTester () =
        inherit NUnitPerf<ISerializer> ()

        let tests = PerfTest.OfModuleMarker<PerformanceTests.Marker> ()

        override __.PerfTests = tests


    type ``Serializer Comparison`` () =
        inherit PerfTester()

        let fsp = FsPickler.initBinary()
        let bfs = new BinaryFormatterSerializer() :> ISerializer
        let ndc = new NetDataContractSerializer() :> ISerializer
        let jdn = new JsonDotNetSerializer() :> ISerializer
        let bdn = new JsonDotNetBsonSerializer () :> ISerializer
        let pbn = new ProtoBufSerializer() :> ISerializer
        let ssj = new ServiceStackJsonSerializer() :> ISerializer
        let sst = new ServiceStackTypeSerializer() :> ISerializer

        let comparer = new WeightedComparer(spaceFactor = 0.2, leastAcceptableImprovementFactor = 1.)
        let tester = new ImplementationComparer<_>(fsp, [bfs;ndc;jdn;bdn;pbn;ssj;sst], throwOnError = true, comparer = comparer)

        override __.PerfTester = tester :> _
        

    type ``FsPickler Formats Comparison`` () =
        inherit PerfTester ()

        let binary = FsPickler.initBinary()
        let json = FsPickler.initJson()
        let bson = FsPickler.initBson()
        let xml = FsPickler.initXml()

        let tester = new ImplementationComparer<_>(binary, [json ; bson; xml], throwOnError = false)

        override __.PerfTester = tester :> _


    type ``Past FsPickler Versions Comparison`` () =
        inherit PerfTester ()

        let persistResults = true
        let persistenceFile = "fspPerf.xml"

        let fsp = FsPickler.initBinary()
        let version = typeof<FsPickler>.Assembly.GetName().Version
        let comparer = new WeightedComparer(spaceFactor = 0.2, leastAcceptableImprovementFactor = 0.8)
        let tester = 
            new PastImplementationComparer<ISerializer>(
                fsp, version, historyFile = persistenceFile, throwOnError = true, comparer = comparer)

        override __.PerfTester = tester :> _

        [<TestFixtureTearDown>]
        member __.Persist() =
            if persistResults then tester.PersistCurrentResults ()