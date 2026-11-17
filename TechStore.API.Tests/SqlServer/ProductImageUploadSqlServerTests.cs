using System.Buffers.Binary;
using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using TechStore.Application.Common;
using TechStore.Application.DTOs.AdminAccounts;
using TechStore.Application.DTOs.Auth;
using TechStore.Application.DTOs.Products;
using TechStore.Application.Interfaces.Storage;

namespace TechStore.API.Tests.SqlServer;

// Every test instance owns its SQL database and storage root. These tests never
// point media mutations or migrations at the development database/storage.
public sealed class ProductImageUploadSqlServerTests : IAsyncLifetime
{
    private readonly SqlServerFixture database = new();
    private readonly string storageId = Guid.NewGuid().ToString("N");
    private readonly string storageParent = Path.GetFullPath(Path.Combine(SolutionRoot(),
        "Frontend", "TechStore.Client", "artifacts", "product-image-sql"));
    private string StorageRoot => Path.Combine(storageParent, storageId);
    private IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["ProductImages:RootPath"] = StorageRoot,
        ["ProductImages:CleanupEnabled"] = "false"
    };

    public Task InitializeAsync() => database.InitializeAsync();
    public async Task DisposeAsync()
    {
        try { await database.DisposeAsync(); }
        finally
        {
            var path = Path.GetFullPath(StorageRoot);
            if (!Guid.TryParseExact(Path.GetFileName(path), "N", out _)
                || !string.Equals(Path.GetDirectoryName(path), storageParent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing to clean a storage directory not owned by this test.");
            if (Directory.Exists(path))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Refusing to recursively clean a reparse point.");
                Directory.Delete(path, recursive: true);
            }
        }
    }

    [SqlServerFact]
    public async Task JpegPngAndWebp_UploadAsUniqueCanonicalWebp_AndRemainPubliclyReadable()
    {
        await using var factory = Factory();
        var (admin, _) = await factory.AdminAsync(); using var owner = admin;
        using var guest = factory.Client();
        var uploadedIds = new HashSet<Guid>();
        foreach (var (format, extension, mime) in new[]
        {
            (SKEncodedImageFormat.Jpeg, ".jpg", "image/jpeg"),
            (SKEncodedImageFormat.Jpeg, ".jpeg", "image/jpeg"),
            (SKEncodedImageFormat.Png, ".png", "image/png"),
            (SKEncodedImageFormat.Webp, ".webp", "image/webp")
        })
        {
            using var response = await UploadResponse(owner, Image(format), "same-name" + extension, mime);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(new[] { "contentType", "height", "id", "imageUrl", "size", "width" },
                json.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
            var upload = (await response.Content.ReadFromJsonAsync<ProductImageUploadDto>())!;
            Assert.True(uploadedIds.Add(upload.Id));
            Assert.Equal($"/api/product-images/{upload.Id:N}.webp", upload.ImageUrl);
            Assert.Equal("image/webp", upload.ContentType);
            Assert.Equal(24, upload.Width);
            Assert.Equal(16, upload.Height);
            var bytes = await File.ReadAllBytesAsync(PhysicalImage(upload.Id));
            Assert.Equal(bytes.LongLength, upload.Size);
            using var codec = SKCodec.Create(new SKMemoryStream(bytes));
            Assert.NotNull(codec);
            Assert.Equal(SKEncodedImageFormat.Webp, codec.EncodedFormat);
            using var firstRead = await guest.GetAsync(upload.ImageUrl);
            Assert.Equal(HttpStatusCode.OK, firstRead.StatusCode);
            Assert.Equal("image/webp", firstRead.Content.Headers.ContentType?.MediaType);
            Assert.Equal("nosniff", Assert.Single(firstRead.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal(bytes, await firstRead.Content.ReadAsByteArrayAsync());
            using var refresh = await guest.GetAsync(upload.ImageUrl);
            Assert.Equal(bytes, await refresh.Content.ReadAsByteArrayAsync());
        }
        Assert.Equal(4, MediaFiles().Length);
    }

    [SqlServerFact]
    public async Task Reencoding_StripsJpegCommentsAndTrailingPayload_WithoutUsingTheOriginalFilename()
    {
        await using var factory = Factory();
        var (admin, _) = await factory.AdminAsync(); using var owner = admin;
        var original = Image(SKEncodedImageFormat.Jpeg);
        var marker = "PRIVATE-METADATA-" + Guid.NewGuid().ToString("N");
        var comment = Encoding.ASCII.GetBytes(marker);
        var input = new MemoryStream();
        input.Write(original.AsSpan(0, 2));
        input.Write(new byte[] { 0xff, 0xfe, (byte)((comment.Length + 2) >> 8), (byte)(comment.Length + 2) });
        input.Write(comment);
        input.Write(original.AsSpan(2));
        input.Write(Encoding.ASCII.GetBytes(marker));

        var upload = await Upload(owner, input.ToArray(), "../../private-file.jpeg", "image/jpeg");

        var file = PhysicalImage(upload.Id);
        Assert.Equal(upload.Id.ToString("N") + ".webp", Path.GetFileName(file));
        Assert.DoesNotContain(marker, Encoding.Latin1.GetString(await File.ReadAllBytesAsync(file)));
        Assert.False(File.Exists(Path.Combine(storageParent, "private-file.jpeg")));
    }

    [SqlServerFact]
    public async Task EveryExifOrientation_IsAppliedToPixelsBeforeMetadataIsRemoved()
    {
        await using var factory = Factory();
        var (admin, _) = await factory.AdminAsync(); using var owner = admin;
        var red = SKColors.Red;
        var green = SKColors.Lime;
        var blue = SKColors.Blue;
        var yellow = SKColors.Yellow;
        var expectedCorners = new[]
        {
            new[] { red, green, blue, yellow },
            new[] { green, red, yellow, blue },
            new[] { yellow, blue, green, red },
            new[] { blue, yellow, red, green },
            new[] { red, blue, green, yellow },
            new[] { blue, red, yellow, green },
            new[] { yellow, green, blue, red },
            new[] { green, yellow, red, blue }
        };
        for (ushort orientation = 1; orientation <= 8; orientation++)
        {
            var input = ExifJpeg(orientation);
            using (var original = SKCodec.Create(new SKMemoryStream(input)))
            {
                Assert.NotNull(original);
                Assert.Equal((SKEncodedOrigin)orientation, original.EncodedOrigin);
            }

            var uploaded = await Upload(owner, input, "oriented.jpeg", "image/jpeg");
            var bytes = await File.ReadAllBytesAsync(PhysicalImage(uploaded.Id));
            using var codec = SKCodec.Create(new SKMemoryStream(bytes));
            Assert.NotNull(codec);
            Assert.Equal(SKEncodedOrigin.TopLeft, codec.EncodedOrigin);
            Assert.Equal(orientation >= 5 ? 16 : 24, uploaded.Width);
            Assert.Equal(orientation >= 5 ? 24 : 16, uploaded.Height);
            using var bitmap = SKBitmap.Decode(bytes);
            Assert.NotNull(bitmap);
            var actual = new[]
            {
                bitmap.GetPixel(bitmap.Width / 4, bitmap.Height / 4),
                bitmap.GetPixel(3 * bitmap.Width / 4, bitmap.Height / 4),
                bitmap.GetPixel(bitmap.Width / 4, 3 * bitmap.Height / 4),
                bitmap.GetPixel(3 * bitmap.Width / 4, 3 * bitmap.Height / 4)
            };
            for (var corner = 0; corner < actual.Length; corner++)
                AssertCloseColor(expectedCorners[orientation - 1][corner], actual[corner]);
            Assert.DoesNotContain("Exif\0\0", Encoding.Latin1.GetString(bytes));
        }
    }

    [SqlServerFact]
    public async Task AnimatedPng_IsRejectedEvenWhenTheNativeDecoderExposesOnlyItsDefaultFrame()
    {
        await using var factory = Factory();
        var (admin, _) = await factory.AdminAsync(); using var owner = admin;
        var animated = AnimatedPng();
        using (var frame = SKBitmap.Decode(animated))
        {
            Assert.NotNull(frame); // The APNG has a real, decodable default PNG frame.
            Assert.Equal(24, frame.Width);
            Assert.Equal(16, frame.Height);
        }

        using var response = await UploadResponse(owner, animated, "animated.png", "image/png");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(MediaFiles());
    }

    [SqlServerFact]
    public async Task UploadAndDraftDeletion_RequireAdmin_WithoutWritingUnauthorizedFiles()
    {
        await using var factory = Factory();
        using var guest = factory.Client();
        var (customer, _) = await factory.CustomerAsync(); using var customerClient = customer;
        using var guestUpload = await UploadResponse(guest, Image(), "photo.png", "image/png");
        Assert.Equal(HttpStatusCode.Unauthorized, guestUpload.StatusCode);
        using var customerUpload = await UploadResponse(customerClient, Image(), "photo.png", "image/png");
        Assert.Equal(HttpStatusCode.Forbidden, customerUpload.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.DeleteAsync($"/api/product-images/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customerClient.DeleteAsync($"/api/product-images/{Guid.NewGuid()}")).StatusCode);
        Assert.Empty(MediaFiles());
    }

    [SqlServerFact]
    public async Task ExecutableSvgTruncatedContentMimeMismatchAndUnsupportedExtensions_AreRejected()
    {
        await using var factory = Factory();
        var (admin, _) = await factory.AdminAsync(); using var owner = admin;
        var png = Image();
        foreach (var (bytes, name, mime) in new[]
        {
            (Encoding.ASCII.GetBytes("MZ executable content"), "renamed.jpg", "image/jpeg"),
            (Encoding.ASCII.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"), "renamed.png", "image/png"),
            (new byte[] { 0xff, 0xd8, 0xff, 0xe0, 0x00 }, "header-only.jpg", "image/jpeg"),
            (png[..(png.Length / 2)], "truncated.png", "image/png"),
            (png, "mismatched.jpg", "image/jpeg"),
            (png, "not-allowed.exe", "image/png"),
            (Array.Empty<byte>(), "empty.png", "image/png")
        })
        {
            using var response = await UploadResponse(owner, bytes, name, mime);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain(StorageRoot, await response.Content.ReadAsStringAsync());
        }
        Assert.Empty(MediaFiles());
    }

    [SqlServerFact]
    public async Task OversizedImageAndMultipartWithoutAFile_AreRejectedWithoutStorageWrites()
    {
        await using var factory = Factory();
        var (admin, _) = await factory.AdminAsync(); using var owner = admin;
        var oversized = new byte[checked((int)ProductImagePolicy.MaximumUploadBytes + 1)];
        using var response = await UploadResponse(owner, oversized, "too-large.png", "image/png");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using var missingFile = new MultipartFormDataContent();
        missingFile.Add(new StringContent("not an image"), "description");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync("/api/product-images", missingFile)).StatusCode);
        Assert.Empty(MediaFiles());
    }

    [SqlServerFact]
    public async Task ExcessiveDimensionsPixelsAndAnimatedWebp_AreRejected()
    {
        await using var factory = Factory();
        var (admin, _) = await factory.AdminAsync(); using var owner = admin;
        using var width = await UploadResponse(owner, Image(width: 4097, height: 1), "too-wide.png", "image/png");
        Assert.Equal(HttpStatusCode.BadRequest, width.StatusCode);
        using var pixels = await UploadResponse(owner, Image(width: 4001, height: 4000), "too-many-pixels.png", "image/png");
        Assert.Equal(HttpStatusCode.BadRequest, pixels.StatusCode);
        var animated = AnimatedWebp();
        using (var codec = SKCodec.Create(new SKMemoryStream(animated)))
        {
            Assert.NotNull(codec);
            Assert.True(codec.FrameCount > 1); // Verify this is a real animation fixture.
        }
        using var animation = await UploadResponse(owner, animated, "animated.webp", "image/webp");
        Assert.Equal(HttpStatusCode.BadRequest, animation.StatusCode);
        Assert.Empty(MediaFiles());
    }

    [SqlServerFact]
    public async Task PublicRead_RejectsTraversalManifestsAndNoncanonicalNames()
    {
        await using var factory = Factory();
        var (admin, _) = await factory.AdminAsync(); using var owner = admin;
        using var guest = factory.Client();
        var upload = await Upload(owner);
        foreach (var path in new[]
        {
            "/api/product-images/%2e%2e%2fmanifests%2fprivate.json",
            "/api/product-images/%5c..%5cprivate.json",
            "/api/product-images/C:%5cWindows%5cwin.ini",
            $"/api/product-images/{upload.Id:N}.json",
            $"/api/product-images/{upload.Id:D}.webp",
            $"/api/product-images/manifests/{upload.Id:N}.json",
            "/api/product-images/%252e%252e%252fprivate.json"
        })
        {
            using var response = await guest.GetAsync(path);
            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.NotFound });
            Assert.DoesNotContain(StorageRoot, await response.Content.ReadAsStringAsync());
        }
        Assert.True(File.Exists(PhysicalImage(upload.Id)));
    }

    [SqlServerFact]
    public async Task MultipleImages_PrimaryAndSortOrder_PersistAndAppearInPublicCatalogueAndDetails()
    {
        await using var factory = Factory();
        var (admin, _) = await factory.AdminAsync(); using var owner = admin;
        using var guest = factory.Client();
        var first = await Upload(owner);
        var second = await Upload(owner, Image(SKEncodedImageFormat.Jpeg), "second.jpeg", "image/jpeg");
        var product = await CreateProduct(owner, new[] { ImageRequest(first, false, 9), ImageRequest(second, true, 2) });

        var refreshed = (await guest.GetFromJsonAsync<ProductResponseDto>($"/api/products/{product.Id}"))!;
        Assert.Equal(new[] { second.ImageUrl, first.ImageUrl }, refreshed.Images.Select(image => image.ImageUrl));
        Assert.Equal(second.ImageUrl, Assert.Single(refreshed.Images, image => image.IsPrimary).ImageUrl);
        var page = (await guest.GetFromJsonAsync<PagedResult<ProductListDto>>("/api/products?Search=" + product.SKU))!;
        Assert.Equal(second.ImageUrl, Assert.Single(page.Items).PrimaryImageUrl);
        foreach (var image in refreshed.Images) Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync(image.ImageUrl)).StatusCode);
        await using var sql = database.Context();
        var rows = await sql.ProductImages.Where(image => image.ProductId == product.Id).OrderBy(image => image.SortOrder).ToListAsync();
        Assert.Equal(new[] { second.ImageUrl, first.ImageUrl }, rows.Select(image => image.ImageUrl));
    }

    [SqlServerFact]
    public async Task MultiplePrimaryImagesOrMoreThanTenImages_DoesNotCreateAProduct()
    {
        await using var factory = Factory();
        var (admin, _) = await factory.AdminAsync(); using var owner = admin;
        var first = await Upload(owner);
        var second = await Upload(owner);
        foreach (var images in new[]
        {
            new[] { ImageRequest(first, true, 0), ImageRequest(second, true, 1) },
            Enumerable.Range(0, 11).Select(index => new ProductImageRequestDto
            { ImageUrl = $"https://images.example.test/isolated-{index}.png", IsPrimary = index == 0, SortOrder = index }).ToArray()
        })
        {
            var dto = await CreateRequest(images);
            using var response = await owner.PostAsJsonAsync("/api/products", dto);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await using var sql = database.Context();
            Assert.False(await sql.Products.AnyAsync(product => product.SKU == dto.SKU));
        }
        Assert.Equal(2, MediaFiles().Length); // Valid drafts remain available for correction/retry.
    }

    [SqlServerFact]
    public async Task ImagesWithoutAnExplicitPrimary_KeepTheExistingLowestSortOrderSelection()
    {
        await using var factory = Factory();
        var (admin, _) = await factory.AdminAsync(); using var owner = admin;
        var first = await Upload(owner);
        var second = await Upload(owner);

        var product = await CreateProduct(owner, new[]
        {
            ImageRequest(first, false, 8), ImageRequest(second, false, 2)
        });

        Assert.Equal(second.ImageUrl, Assert.Single(product.Images, image => image.IsPrimary).ImageUrl);
        await using var sql = database.Context();
        Assert.Equal(second.ImageUrl, (await sql.ProductImages.SingleAsync(image => image.ProductId == product.Id && image.IsPrimary)).ImageUrl);
    }

    [SqlServerFact]
    public async Task OmittedImagesOnUpdate_PreserveExistingImagesAndPhysicalFiles()
    {
        await using var factory = Factory();
        var (admin, _) = await factory.AdminAsync(); using var owner = admin;
        var upload = await Upload(owner);
        var product = await CreateProduct(owner, new[] { ImageRequest(upload, true, 0) });

        using var response = await owner.PutAsJsonAsync($"/api/products/{product.Id}", UpdateBody(product, "Renamed without replacing images"));

        response.EnsureSuccessStatusCode();
        var updated = (await response.Content.ReadFromJsonAsync<ProductResponseDto>())!;
        Assert.Equal(upload.ImageUrl, Assert.Single(updated.Images).ImageUrl);
        Assert.True(File.Exists(PhysicalImage(upload.Id)));
        await using var sql = database.Context();
        Assert.Equal(upload.ImageUrl, (await sql.ProductImages.SingleAsync(image => image.ProductId == product.Id)).ImageUrl);
    }

    [SqlServerFact]
    public async Task ReorderingAndExplicitRemoval_CommitReferencesBeforeRemovingOnlyTheUnreferencedFile()
    {
        await using var factory = Factory();
        var (admin, _) = await factory.AdminAsync(); using var owner = admin;
        var first = await Upload(owner);
        var second = await Upload(owner);
        var product = await CreateProduct(owner, new[] { ImageRequest(first, true, 0), ImageRequest(second, false, 1) });
        using var reorderedResponse = await owner.PutAsJsonAsync($"/api/products/{product.Id}",
            UpdateRequest(product, new[] { ImageRequest(second, true, 0), ImageRequest(first, false, 1) }));
        reorderedResponse.EnsureSuccessStatusCode();
        var reordered = (await reorderedResponse.Content.ReadFromJsonAsync<ProductResponseDto>())!;
        Assert.Equal(second.ImageUrl, reordered.Images[0].ImageUrl);
        Assert.True(reordered.Images[0].IsPrimary);
        Assert.True(File.Exists(PhysicalImage(first.Id)));
        Assert.True(File.Exists(PhysicalImage(second.Id)));

        using var removal = await owner.PutAsJsonAsync($"/api/products/{product.Id}",
            UpdateRequest(reordered, new[] { ImageRequest(second, true, 0) }));
        removal.EnsureSuccessStatusCode();
        await using var sql = database.Context();
        Assert.Equal(second.ImageUrl, (await sql.ProductImages.SingleAsync(image => image.ProductId == product.Id)).ImageUrl);
        Assert.False(File.Exists(PhysicalImage(first.Id)));
        Assert.True(File.Exists(PhysicalImage(second.Id)));
    }

    [SqlServerFact]
    public async Task SoftDeletingAProduct_PreservesItsImagesAndPreventsDraftDeletion()
    {
        await using var factory = Factory();
        var (admin, _) = await factory.AdminAsync(); using var owner = admin;
        var upload = await Upload(owner);
        var product = await CreateProduct(owner, new[] { ImageRequest(upload, true, 0) });
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/products/{product.Id}");
        request.Headers.IfMatch.Add(new EntityTagHeaderValue('"' + Convert.ToBase64String(product.RowVersion) + '"'));
        using var response = await owner.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(File.Exists(PhysicalImage(upload.Id)));
        Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync($"/api/product-images/{upload.Id}")).StatusCode);
        await using var sql = database.Context();
        Assert.True((await sql.Products.FindAsync(product.Id))!.IsDeleted);
        Assert.True(await sql.ProductImages.AnyAsync(image => image.ProductId == product.Id && image.ImageUrl == upload.ImageUrl));
    }

    [SqlServerFact]
    public async Task ASharedManagedImage_IsDeletedOnlyWhenTheLastProductReferenceIsRemoved()
    {
        await using var factory = Factory();
        var (admin, _) = await factory.AdminAsync(); using var owner = admin;
        var upload = await Upload(owner);
        var first = await CreateProduct(owner, new[] { ImageRequest(upload, true, 0) });
        var second = await CreateProduct(owner, new[] { ImageRequest(upload, true, 0) });

        (await owner.PutAsJsonAsync($"/api/products/{first.Id}", UpdateRequest(first, Array.Empty<ProductImageRequestDto>()))).EnsureSuccessStatusCode();
        Assert.True(File.Exists(PhysicalImage(upload.Id)));
        Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync($"/api/product-images/{upload.Id}")).StatusCode);
        (await owner.PutAsJsonAsync($"/api/products/{second.Id}", UpdateRequest(second, Array.Empty<ProductImageRequestDto>()))).EnsureSuccessStatusCode();
        Assert.False(File.Exists(PhysicalImage(upload.Id)));
        await using var sql = database.Context();
        Assert.False(await sql.ProductImages.AnyAsync(image => image.ImageUrl == upload.ImageUrl));
    }

    [SqlServerFact]
    public async Task StaleProductUpdate_PreservesExistingFileReferencesAndTheNewDraftForRetry()
    {
        await using var factory = Factory();
        var (admin, _) = await factory.AdminAsync(); using var owner = admin;
        var oldImage = await Upload(owner);
        var draft = await Upload(owner);
        var product = await CreateProduct(owner, new[] { ImageRequest(oldImage, true, 0) });
        (await owner.PatchAsJsonAsync($"/api/products/{product.Id}/stock", new UpdateStockDto
        { QuantityChange = 1, RowVersion = product.RowVersion })).EnsureSuccessStatusCode();

        var response = await owner.PutAsJsonAsync($"/api/products/{product.Id}", UpdateRequest(product, new[] { ImageRequest(draft, true, 0) }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(File.Exists(PhysicalImage(oldImage.Id)));
        Assert.True(File.Exists(PhysicalImage(draft.Id)));
        await using var sql = database.Context();
        Assert.Equal(oldImage.ImageUrl, (await sql.ProductImages.SingleAsync(image => image.ProductId == product.Id)).ImageUrl);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/product-images/{draft.Id}")).StatusCode);
    }

    [SqlServerFact]
    public async Task SqlFailureDuringImageUpdate_RollsBackRowsAndPreservesOldAndPendingFiles()
    {
        var failure = new FailProductUpdateInSql();
        await using var factory = Factory(failure);
        var (admin, _) = await factory.AdminAsync(); using var owner = admin;
        var oldImage = await Upload(owner);
        var draft = await Upload(owner);
        var product = await CreateProduct(owner, new[] { ImageRequest(oldImage, true, 0) });
        failure.Armed = true;

        using var response = await owner.PutAsJsonAsync($"/api/products/{product.Id}",
            UpdateRequest(product, new[] { ImageRequest(draft, true, 0) }, "Must roll back"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(failure.Injected);
        Assert.True(File.Exists(PhysicalImage(oldImage.Id)));
        Assert.True(File.Exists(PhysicalImage(draft.Id)));
        await using var sql = database.Context();
        var persisted = (await sql.Products.AsNoTracking().Include(item => item.Images).SingleAsync(item => item.Id == product.Id));
        Assert.Equal(product.Name, persisted.Name);
        Assert.Equal(product.RowVersion, persisted.RowVersion);
        Assert.Equal(oldImage.ImageUrl, Assert.Single(persisted.Images).ImageUrl);
    }

    [SqlServerFact]
    public async Task DraftDeletion_IsOwnerOnly_AndCannotDeleteAnAttachedImage()
    {
        await using var factory = Factory();
        var (admin, _) = await factory.AdminAsync(); using var owner = admin;
        var upload = await Upload(owner);
        using var other = await OtherAdmin(factory, owner);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/product-images/{upload.Id}")).StatusCode);
        Assert.True(File.Exists(PhysicalImage(upload.Id)));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/product-images/{upload.Id}")).StatusCode);
        Assert.False(File.Exists(PhysicalImage(upload.Id)));
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(upload.ImageUrl)).StatusCode);
        var attached = await Upload(owner);
        await CreateProduct(owner, new[] { ImageRequest(attached, true, 0) });
        Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync($"/api/product-images/{attached.Id}")).StatusCode);
        Assert.True(File.Exists(PhysicalImage(attached.Id)));
    }

    [SqlServerFact]
    public async Task ConcurrentProductCreationAndDraftDeletion_NeverCommitABrokenReference()
    {
        await using var factory = Factory();
        var (admin, _) = await factory.AdminAsync(); using var owner = admin;
        var upload = await Upload(owner);
        var request = await CreateRequest(new[] { ImageRequest(upload, true, 0) });

        var creating = owner.PostAsJsonAsync("/api/products", request);
        var deleting = owner.DeleteAsync($"/api/product-images/{upload.Id}");
        await Task.WhenAll(creating, deleting);
        using var created = await creating;
        using var deleted = await deleting;
        await using var sql = database.Context();
        if (created.StatusCode == HttpStatusCode.Created)
        {
            Assert.Equal(HttpStatusCode.Conflict, deleted.StatusCode);
            Assert.True(File.Exists(PhysicalImage(upload.Id)));
            var product = await sql.Products.AsNoTracking().Include(item => item.Images).SingleAsync(item => item.SKU == request.SKU);
            Assert.Equal(upload.ImageUrl, Assert.Single(product.Images).ImageUrl);
        }
        else
        {
            Assert.Contains(created.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Conflict, HttpStatusCode.NotFound });
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            Assert.False(await sql.Products.AnyAsync(item => item.SKU == request.SKU));
            Assert.False(File.Exists(PhysicalImage(upload.Id)));
        }
    }

    [SqlServerFact]
    public async Task ExpiredDraftCleanup_DeletesOnlyOldOwnedDrafts_AndIsIdempotent()
    {
        await using var factory = Factory();
        var (admin, _) = await factory.AdminAsync(); using var owner = admin;
        var expired = await Upload(owner);
        var recent = await Upload(owner);
        var cutoff = DateTime.UtcNow.AddHours(-24);
        await EditManifest(expired, manifest => manifest["createdAtUtc"] = JsonValue.Create(new DateTimeOffset(cutoff.AddHours(-1))));
        var foreign = Path.Combine(StorageRoot, "not-managed-by-the-store.txt");
        await File.WriteAllTextAsync(foreign, "Keep this foreign file.");
        var unowned = PhysicalImage(Guid.NewGuid());
        Directory.CreateDirectory(Path.GetDirectoryName(unowned)!);
        var unownedBytes = Image(SKEncodedImageFormat.Webp);
        await File.WriteAllBytesAsync(unowned, unownedBytes); // No ownership manifest.

        Assert.Equal(1, await Cleanup(factory, cutoff));

        Assert.False(File.Exists(PhysicalImage(expired.Id)));
        Assert.False(File.Exists(ManifestPath(expired.Id)));
        Assert.True(File.Exists(PhysicalImage(recent.Id)));
        Assert.True(File.Exists(ManifestPath(recent.Id)));
        Assert.Equal("Keep this foreign file.", await File.ReadAllTextAsync(foreign));
        Assert.Equal(unownedBytes, await File.ReadAllBytesAsync(unowned));
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(expired.ImageUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(recent.ImageUrl)).StatusCode);
        Assert.Equal(0, await Cleanup(factory, cutoff));
    }

    [SqlServerFact]
    public async Task ExpiredDraftCleanup_PreservesAttachedAndSqlReferencedImages_IncludingSoftDeletedProducts()
    {
        await using var factory = Factory();
        var (admin, _) = await factory.AdminAsync(); using var owner = admin;
        var attached = await Upload(owner);
        var referenced = await Upload(owner);
        var deletedReference = await Upload(owner);
        var attachedMarkerOnly = await Upload(owner);
        await CreateProduct(owner, new[] { ImageRequest(attached, true, 0), ImageRequest(referenced, false, 1) });
        var deletedProduct = await CreateProduct(owner, new[] { ImageRequest(deletedReference, true, 0) });
        using (var deletion = new HttpRequestMessage(HttpMethod.Delete, $"/api/products/{deletedProduct.Id}"))
        {
            deletion.Headers.IfMatch.Add(new EntityTagHeaderValue('"' + Convert.ToBase64String(deletedProduct.RowVersion) + '"'));
            Assert.Equal(HttpStatusCode.NoContent, (await owner.SendAsync(deletion)).StatusCode);
        }
        var cutoff = DateTime.UtcNow.AddHours(-24);
        foreach (var image in new[] { attached, referenced, deletedReference, attachedMarkerOnly })
            await EditManifest(image, manifest =>
            {
                manifest["createdAtUtc"] = JsonValue.Create(new DateTimeOffset(cutoff.AddHours(-1)));
                // Simulate marker persistence failing after a real SQL commit.
                manifest["attached"] = image.Id == attached.Id || image.Id == attachedMarkerOnly.Id;
            });

        Assert.Equal(0, await Cleanup(factory, cutoff));

        foreach (var image in new[] { attached, referenced, deletedReference, attachedMarkerOnly })
        {
            Assert.True(File.Exists(PhysicalImage(image.Id)));
            Assert.True(File.Exists(ManifestPath(image.Id)));
        }
        await using var sql = database.Context();
        Assert.True((await sql.Products.FindAsync(deletedProduct.Id))!.IsDeleted);
        Assert.Equal(3, await sql.ProductImages.CountAsync(image => image.ImageUrl == attached.ImageUrl
            || image.ImageUrl == referenced.ImageUrl || image.ImageUrl == deletedReference.ImageUrl));
    }

    [SqlServerFact]
    public async Task ExpiredDraftCleanup_RespectsTheDeletionBatchLimitAcrossRepeatedRuns()
    {
        await using var factory = Factory();
        var (admin, _) = await factory.AdminAsync(); using var owner = admin;
        var cutoff = DateTime.UtcNow.AddHours(-24);
        for (var index = 0; index < 3; index++)
        {
            var image = await Upload(owner);
            await EditManifest(image, manifest => manifest["createdAtUtc"] = JsonValue.Create(new DateTimeOffset(cutoff.AddDays(-1))));
        }

        for (var remaining = 2; remaining >= 0; remaining--)
        {
            Assert.Equal(1, await Cleanup(factory, cutoff, 1));
            Assert.Equal(remaining, MediaFiles().Length);
        }
        Assert.Equal(0, await Cleanup(factory, cutoff, 1));
    }

    [SqlServerFact]
    public async Task ExpiredDraftCleanup_SkipsCorruptOrInvalidManifests_AndContinuesWithValidDrafts()
    {
        await using var factory = Factory();
        var (admin, _) = await factory.AdminAsync(); using var owner = admin;
        var corrupt = await Upload(owner);
        var wrongId = await Upload(owner);
        var invalidTime = await Upload(owner);
        var invalidSize = await Upload(owner);
        var valid = await Upload(owner);
        var cutoff = DateTime.UtcNow.AddHours(-24);
        foreach (var image in new[] { corrupt, wrongId, invalidTime, invalidSize, valid })
            await EditManifest(image, manifest => manifest["createdAtUtc"] = JsonValue.Create(new DateTimeOffset(cutoff.AddHours(-2))));
        await File.WriteAllTextAsync(ManifestPath(corrupt.Id), "{ invalid JSON");
        await EditManifest(wrongId, manifest => manifest["id"] = JsonValue.Create(Guid.NewGuid()));
        await EditManifest(invalidTime, manifest => manifest["createdAtUtc"] = "not a UTC timestamp");
        await EditManifest(invalidSize, manifest => manifest["size"] = -1);

        Assert.Equal(1, await Cleanup(factory, cutoff));

        Assert.False(File.Exists(PhysicalImage(valid.Id)));
        foreach (var image in new[] { corrupt, wrongId, invalidTime, invalidSize })
        {
            Assert.True(File.Exists(PhysicalImage(image.Id)));
            Assert.True(File.Exists(ManifestPath(image.Id)));
        }
    }

    [SqlServerFact]
    public async Task ExpiredDraftCleanup_ValidatesAndCancelsWithoutCreatingStorageDirectories()
    {
        await using var factory = Factory();
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProductImageStore>();
        var cutoff = DateTime.UtcNow.AddHours(-24);
        await Assert.ThrowsAsync<ArgumentException>(() => store.CleanupExpiredDraftsAsync(DateTime.SpecifyKind(cutoff, DateTimeKind.Unspecified), 100));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.CleanupExpiredDraftsAsync(cutoff, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.CleanupExpiredDraftsAsync(cutoff, 1001));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CleanupExpiredDraftsAsync(cutoff, 100, canceled.Token));
        Assert.Equal(0, await store.CleanupExpiredDraftsAsync(cutoff, 100));
        Assert.False(Directory.Exists(StorageRoot));
    }

    [SqlServerFact]
    public async Task ExpiredDraftCleanup_RetainsFilesWhenTheSqlReferenceCheckFails_AndCanRetry()
    {
        var failure = new FailReferenceLookup();
        await using var factory = Factory(failure);
        var (admin, _) = await factory.AdminAsync(); using var owner = admin;
        var image = await Upload(owner);
        var cutoff = DateTime.UtcNow.AddHours(-24);
        await EditManifest(image, manifest => manifest["createdAtUtc"] = JsonValue.Create(new DateTimeOffset(cutoff.AddHours(-1))));
        failure.Armed = true;

        Assert.Equal(0, await Cleanup(factory, cutoff));

        Assert.True(failure.Attempts > 0);
        Assert.True(File.Exists(PhysicalImage(image.Id)));
        Assert.True(File.Exists(ManifestPath(image.Id)));
        failure.Armed = false;
        Assert.Equal(1, await Cleanup(factory, cutoff));
        Assert.False(File.Exists(PhysicalImage(image.Id)));
    }

    [SqlServerFact]
    public async Task ExpiredDraftCleanup_DoesNotFollowAReparsePointIntoForeignManifests()
    {
        await using var factory = Factory();
        var (admin, _) = await factory.AdminAsync(); using var owner = admin;
        var valid = await Upload(owner);
        var cutoff = DateTime.UtcNow.AddHours(-24);
        await EditManifest(valid, manifest => manifest["createdAtUtc"] = JsonValue.Create(new DateTimeOffset(cutoff.AddHours(-1))));
        var foreignId = Guid.NewGuid();
        while (foreignId.ToString("N")[..2] == valid.Id.ToString("N")[..2]) foreignId = Guid.NewGuid();
        var target = Path.Combine(StorageRoot, "foreign-manifests");
        Directory.CreateDirectory(target);
        var link = Path.Combine(StorageRoot, "manifests", foreignId.ToString("N")[..2]);
        var foreignManifest = Path.Combine(target, foreignId.ToString("N") + ".json");
        var metadata = JsonNode.Parse(await File.ReadAllTextAsync(ManifestPath(valid.Id)))!.AsObject();
        metadata["id"] = JsonValue.Create(foreignId);
        await File.WriteAllTextAsync(foreignManifest, metadata.ToJsonString());
        var foreignImage = PhysicalImage(foreignId);
        Directory.CreateDirectory(Path.GetDirectoryName(foreignImage)!);
        var foreignBytes = await File.ReadAllBytesAsync(PhysicalImage(valid.Id));
        await File.WriteAllBytesAsync(foreignImage, foreignBytes);
        await CreateDirectoryLink(link, target);
        try
        {
            Assert.Equal(1, await Cleanup(factory, cutoff));

            Assert.False(File.Exists(PhysicalImage(valid.Id)));
            Assert.Equal(foreignBytes, await File.ReadAllBytesAsync(foreignImage));
            Assert.True(File.Exists(foreignManifest));
            Assert.Equal(0, await Cleanup(factory, cutoff));
        }
        finally
        {
            RequireInsideStorage(link);
            if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0)
                throw new InvalidOperationException("Refusing to remove an unexpected non-link directory.");
            Directory.Delete(link, recursive: false); // Remove only the link before normal fixture cleanup.
        }
    }

    private SqlApiFactory Factory(IInterceptor? interceptor = null) => new(database, interceptor, Settings);
    private string PhysicalImage(Guid id) => Path.Combine(StorageRoot, "files", id.ToString("N")[..2], id.ToString("N") + ".webp");
    private string ManifestPath(Guid id) => Path.Combine(StorageRoot, "manifests", id.ToString("N")[..2], id.ToString("N") + ".json");
    private string[] MediaFiles() => Directory.Exists(StorageRoot) ? Directory.GetFiles(StorageRoot, "*.webp", SearchOption.AllDirectories) : [];

    private async Task EditManifest(ProductImageUploadDto image, Action<JsonObject> edit)
    {
        var path = ManifestPath(image.Id);
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        edit(manifest);
        await File.WriteAllTextAsync(path, manifest.ToJsonString());
    }
    private static async Task<int> Cleanup(SqlApiFactory factory, DateTime cutoffUtc, int batchSize = 100)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IProductImageStore>().CleanupExpiredDraftsAsync(cutoffUtc, batchSize);
    }
    private void RequireInsideStorage(string path)
    {
        var absolute = Path.GetFullPath(path);
        if (!absolute.StartsWith(Path.GetFullPath(StorageRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The test path must stay inside its owned storage directory.");
    }
    private async Task CreateDirectoryLink(string link, string target)
    {
        RequireInsideStorage(link);
        RequireInsideStorage(target);
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        // NTFS junctions work without the symbolic-link privilege or Developer Mode.
        // Both absolute paths have been checked above; the shell only creates the link.
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not create the isolated test junction.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await output;
        Assert.True(process.ExitCode == 0, "Could not create the isolated test junction: " + await error);
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
    }

    private static async Task<HttpResponseMessage> UploadResponse(HttpClient client, byte[] bytes, string fileName, string contentType)
    {
        using var multipart = new MultipartFormDataContent();
        using var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        multipart.Add(file, "file", fileName);
        return await client.PostAsync("/api/product-images", multipart);
    }
    private static async Task<ProductImageUploadDto> Upload(HttpClient client, byte[]? bytes = null, string fileName = "photo.png", string contentType = "image/png")
    {
        using var response = await UploadResponse(client, bytes ?? Image(), fileName, contentType);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProductImageUploadDto>())!;
    }
    private async Task<CreateProductDto> CreateRequest(IEnumerable<ProductImageRequestDto> images)
    {
        await using var sql = database.Context();
        var categoryId = await sql.Categories.Where(category => category.IsActive).Select(category => category.Id).FirstAsync();
        return new() { Name = "Image SQL product", Description = "Isolated upload test", SKU = "IMG-" + Guid.NewGuid().ToString("N"),
            Brand = "Test brand", Price = 100m, Stock = 5, CategoryId = categoryId, IsActive = true, Images = images.ToList() };
    }
    private async Task<ProductResponseDto> CreateProduct(HttpClient client, IEnumerable<ProductImageRequestDto> images)
    {
        using var response = await client.PostAsJsonAsync("/api/products", await CreateRequest(images));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProductResponseDto>())!;
    }
    private static ProductImageRequestDto ImageRequest(ProductImageUploadDto upload, bool primary, int order) =>
        new() { ImageUrl = upload.ImageUrl, IsPrimary = primary, SortOrder = order };
    private static UpdateProductDto UpdateRequest(ProductResponseDto current, IEnumerable<ProductImageRequestDto> images, string? name = null) =>
        new() { Name = name ?? current.Name, Description = current.Description, SKU = current.SKU, Brand = current.Brand,
            Price = current.Price, DiscountPrice = current.DiscountPrice, Stock = current.Stock, CategoryId = current.CategoryId,
            IsActive = current.IsActive, RowVersion = current.RowVersion, Images = images.ToList() };
    private static object UpdateBody(ProductResponseDto current, string name) => new
    {
        name, description = current.Description, sku = current.SKU, brand = current.Brand, price = current.Price,
        discountPrice = current.DiscountPrice, stock = current.Stock, categoryId = current.CategoryId,
        isActive = current.IsActive, rowVersion = current.RowVersion
    };
    private static async Task<HttpClient> OtherAdmin(SqlApiFactory factory, HttpClient creator)
    {
        var email = Guid.NewGuid().ToString("N") + "@image-sql.example.test";
        var password = Guid.NewGuid().ToString("N") + "Aa1!";
        (await creator.PostAsJsonAsync("/api/admin/admins", new CreateAdminDto { FullName = "Other image Admin", Email = email, Password = password })).EnsureSuccessStatusCode();
        var client = factory.Client();
        using var response = await client.PostAsJsonAsync("/api/auth/login", new LoginDto { Email = email, Password = password });
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponseDto>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }
    private static byte[] Image(SKEncodedImageFormat format = SKEncodedImageFormat.Png, int width = 24, int height = 16)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Orange);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(format, 90);
        return encoded.ToArray();
    }

    private static byte[] ExifJpeg(ushort orientation)
    {
        using var bitmap = new SKBitmap(24, 16);
        var colors = new[] { SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.Yellow };
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                bitmap.SetPixel(x, y, colors[(y >= bitmap.Height / 2 ? 2 : 0) + (x >= bitmap.Width / 2 ? 1 : 0)]);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 100);
        var jpeg = encoded.ToArray();
        // Real little-endian TIFF IFD0 with one SHORT Orientation tag (0x0112).
        var exif = new byte[32];
        "Exif\0\0"u8.CopyTo(exif);
        "II"u8.CopyTo(exif.AsSpan(6));
        BinaryPrimitives.WriteUInt16LittleEndian(exif.AsSpan(8), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(exif.AsSpan(10), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(exif.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(exif.AsSpan(16), 0x0112);
        BinaryPrimitives.WriteUInt16LittleEndian(exif.AsSpan(18), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(exif.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(exif.AsSpan(24), orientation);
        using var result = new MemoryStream();
        result.Write(jpeg.AsSpan(0, 2));
        result.Write(new byte[] { 0xff, 0xe1, 0, checked((byte)(exif.Length + 2)) });
        result.Write(exif);
        result.Write(jpeg.AsSpan(2));
        return result.ToArray();
    }
    private static void AssertCloseColor(SKColor expected, SKColor actual)
    {
        // JPEG input and WebP output are lossy; separated corner samples must
        // preserve the correct colored quadrant without demanding exact bytes.
        Assert.InRange(Math.Abs((int)expected.Red - actual.Red), 0, 45);
        Assert.InRange(Math.Abs((int)expected.Green - actual.Green), 0, 45);
        Assert.InRange(Math.Abs((int)expected.Blue - actual.Blue), 0, 45);
        Assert.Equal((byte)255, actual.Alpha);
    }
    private static byte[] AnimatedPng()
    {
        var still = Image();
        var data = new MemoryStream();
        byte[]? header = null;
        for (var offset = 8; offset + 12 <= still.Length;)
        {
            var size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(still.AsSpan(offset, 4)));
            var type = Encoding.ASCII.GetString(still, offset + 4, 4);
            if (type == "IHDR") header = still.AsSpan(offset + 8, size).ToArray();
            if (type == "IDAT") data.Write(still.AsSpan(offset + 8, size));
            offset += size + 12;
        }
        Assert.NotNull(header);
        Assert.True(data.Length > 0);
        using var result = new MemoryStream();
        result.Write(still.AsSpan(0, 8));
        WritePngChunk(result, "IHDR", header);
        var animation = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(animation, 2); // Two real frames, infinite playback.
        WritePngChunk(result, "acTL", animation);
        var frame = new byte[26];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), 24);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8), 16);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(22), 10);
        WritePngChunk(result, "fcTL", frame); // Sequence 0, default frame.
        WritePngChunk(result, "IDAT", data.ToArray());
        BinaryPrimitives.WriteUInt32BigEndian(frame, 1);
        WritePngChunk(result, "fcTL", frame);
        var frameData = new byte[checked((int)data.Length + 4)];
        BinaryPrimitives.WriteUInt32BigEndian(frameData, 2);
        data.ToArray().CopyTo(frameData, 4);
        WritePngChunk(result, "fdAT", frameData);
        WritePngChunk(result, "IEND", []);
        return result.ToArray();
    }
    private static void WritePngChunk(Stream output, string type, byte[] payload)
    {
        Span<byte> integer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(integer, checked((uint)payload.Length));
        output.Write(integer);
        var name = Encoding.ASCII.GetBytes(type);
        output.Write(name);
        output.Write(payload);
        var crc = 0xffff_ffffu;
        foreach (var value in name.Concat(payload))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb8_8320u : crc >> 1;
        }
        BinaryPrimitives.WriteUInt32BigEndian(integer, ~crc);
        output.Write(integer);
    }

    private static byte[] AnimatedWebp()
    {
        const int width = 24, height = 16;
        var still = Image(SKEncodedImageFormat.Webp, width, height);
        var frameChunks = new MemoryStream();
        for (var offset = 12; offset + 8 <= still.Length;)
        {
            var name = Encoding.ASCII.GetString(still, offset, 4);
            var size = BitConverter.ToInt32(still, offset + 4);
            var length = 8 + size + (size & 1);
            if (name is "VP8 " or "VP8L" or "ALPH") frameChunks.Write(still.AsSpan(offset, length));
            offset += length;
        }
        var frameHeader = new byte[16];
        frameHeader[6] = width - 1;
        frameHeader[9] = height - 1;
        frameHeader[12] = 100;
        frameHeader[15] = 2;
        var frame = frameHeader.Concat(frameChunks.ToArray()).ToArray();
        using var body = new MemoryStream();
        body.Write(Encoding.ASCII.GetBytes("WEBP"));
        var extended = new byte[10];
        extended[0] = 2;
        extended[4] = width - 1;
        extended[7] = height - 1;
        WriteChunk(body, "VP8X", extended);
        WriteChunk(body, "ANIM", new byte[6]);
        WriteChunk(body, "ANMF", frame);
        WriteChunk(body, "ANMF", frame);
        using var result = new MemoryStream();
        result.Write(Encoding.ASCII.GetBytes("RIFF"));
        result.Write(BitConverter.GetBytes(checked((int)body.Length)));
        body.Position = 0;
        body.CopyTo(result);
        return result.ToArray();
    }
    private static void WriteChunk(Stream output, string name, byte[] bytes)
    {
        output.Write(Encoding.ASCII.GetBytes(name));
        output.Write(BitConverter.GetBytes(bytes.Length));
        output.Write(bytes);
        if ((bytes.Length & 1) != 0) output.WriteByte(0);
    }
    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TechStore.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Cannot locate the solution for isolated media storage.");
    }
    private sealed class FailProductUpdateInSql : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public bool Injected { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("UPDATE [Products]", StringComparison.OrdinalIgnoreCase))
            {
                Armed = false;
                Injected = true;
                command.CommandText += "\n;THROW 51000, 'Injected isolated image update SQL failure.', 1;";
            }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class FailReferenceLookup : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public int Attempts { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("FROM [ProductImages]", StringComparison.OrdinalIgnoreCase))
            {
                Attempts++;
                throw new InjectedReferenceLookupFailure();
            }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class InjectedReferenceLookupFailure : DbException
    {
        public InjectedReferenceLookupFailure() : base("Injected isolated reference lookup failure.") { }
    }
}
