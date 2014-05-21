﻿using System;
using System.Collections.Generic;

namespace NuGet.Services.Metadata.Catalog.Maintenance
{
    class CatalogPage : CatalogContainer
    {
        IDictionary<Uri, Tuple<Uri, DateTime, int?>> _items;

        public CatalogPage(Uri page, Uri root, string content = null)
            : base(page, root)
        {
            _items = new Dictionary<Uri, Tuple<Uri, DateTime, int?>>();
            if (content != null)
            {
                Load(_items, content);
            }
        }

        public void Add(Uri resourceUri, Uri resourceType, DateTime timeStamp)
        {
            _items.Add(resourceUri, new Tuple<Uri, DateTime, int?>(resourceType, timeStamp, null));
        }

        protected override Uri GetContainerType()
        {
            return Constants.CatalogPage;
        }

        protected override IDictionary<Uri, Tuple<Uri, DateTime, int?>> GetItems()
        {
            return _items;
        }
    }
}
