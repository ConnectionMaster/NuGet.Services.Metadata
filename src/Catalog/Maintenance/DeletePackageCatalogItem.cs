﻿using Newtonsoft.Json.Linq;
using NuGet.Services.Metadata.Catalog.Persistence;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VDS.RDF;

namespace NuGet.Services.Metadata.Catalog.Maintenance
{
    public class DeletePackageCatalogItem : CatalogItem
    {
        string _id;
        string _version;

        public DeletePackageCatalogItem(string id, string version)
        {
            _id = id;
            _version = version;
        }

        public override StorageContent CreateContent(CatalogContext context)
        {
            IGraph graph = new Graph();

            graph.NamespaceMap.AddNamespace("rdf", new Uri("http://www.w3.org/1999/02/22-rdf-syntax-ns#"));
            graph.NamespaceMap.AddNamespace("nuget", new Uri("http://nuget.org/schema#"));

            INode subject = graph.CreateUriNode(new Uri(GetBaseAddress() + GetItemIdentity()));

            graph.Assert(subject, graph.CreateUriNode("rdf:type"), graph.CreateUriNode("nuget:DeletePackage"));
            graph.Assert(subject, graph.CreateUriNode("nuget:id"), graph.CreateLiteralNode(_id));
            graph.Assert(subject, graph.CreateUriNode("nuget:version"), graph.CreateLiteralNode(_version));

            JObject frame = context.GetJsonLdContext("context.DeletePackage.json", GetItemType());

            StorageContent content = new StringStorageContent(Utils.CreateJson(graph, frame), "application/json");

            return content;
        }

        public override Uri GetItemType()
        {
            return Constants.DeletePackage;
        }

        protected override string GetItemIdentity()
        {
            return "delete/" + _id + "." + _version;
        }
    }
}
