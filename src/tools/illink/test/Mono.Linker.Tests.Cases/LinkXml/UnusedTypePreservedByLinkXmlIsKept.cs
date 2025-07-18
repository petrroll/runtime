﻿using Mono.Linker.Tests.Cases.Expectations.Assertions;
using Mono.Linker.Tests.Cases.Expectations.Metadata;

namespace Mono.Linker.Tests.Cases.LinkXml
{
    [SetupLinkerDescriptorFile("UnusedTypePreservedByLinkXmlIsKept.xml")]
    class UnusedTypePreservedByLinkXmlIsKept
    {
        public static void Main()
        {
        }
    }

    [Kept]
    [KeptMember(".ctor()")]
    class UnusedTypePreservedByLinkXmlIsKeptUnusedType
    {
    }

    [Kept]
    public class UnusedGenericTypePreservedByLinkXmlIsKept<T>
    {
        [Kept]
        public class UnusedNestedTypePreservedByLinkXmlIsKept { }

        [Kept]
        public class UnusedNestedGenericTypePreservedByLinkXmlIsKept<U, V>
        {
            [Kept]
            public class UnusedNestedNestedGenericTypePreservedByLinkXmlIsKept<W> { }
        }

        public class UnusedNestedTypeNotPreserved { }
    }
}
