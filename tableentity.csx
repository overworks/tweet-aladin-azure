#r "Microsoft.WindowsAzure.Storage"

using Microsoft.WindowsAzure.Storage.Table;

public class BookEntity : TableEntity
{
    public string Name { get; set; }
}
