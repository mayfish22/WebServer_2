using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using System.Globalization;
using System.Net;
using System.Text;
using WebServer.Helpers;
using WebServer.Models;

namespace WebServer.Controllers
{
    public class StreamingController : Controller
    {
        private readonly ILogger<StreamingController> _logger;
        private static readonly FormOptions _defaultFormOptions = new FormOptions();
        private string[] _permittedExtensions = new string[] { ".jpg", ".png"}; //允許的檔案類型
        private long _fileSizeLimit = 50 * 1024 * 1024; // 50MB, 檔案大小限制
        private string _targetFilePath; // 儲存路徑

        public StreamingController(ILogger<StreamingController> logger)
        {
            _logger = logger;
            _targetFilePath = Path.GetTempPath();

            _logger.LogInformation("FilePath: " + _targetFilePath);
        }

        [HttpPost]
        [DisableFormValueModelBinding]
        public async Task<IActionResult> Upload()
        {
            try
            {
                if (!MultipartRequestHelper.IsMultipartContentType(Request.ContentType))
                {
                    ModelState.AddModelError("File",
                        $"The request couldn't be processed (Error 1).");
                    // Log error

                    return BadRequest(ModelState);
                }

                // Accumulate the form data key-value pairs in the request (formAccumulator).
                var formAccumulator = new KeyValueAccumulator();
                var trustedFileNameForDisplay = string.Empty;
                var untrustedFileNameForStorage = string.Empty;
                var streamedFileContent = Array.Empty<byte>();

                var boundary = MultipartRequestHelper.GetBoundary(
                    MediaTypeHeaderValue.Parse(Request.ContentType),
                    _defaultFormOptions.MultipartBoundaryLengthLimit);
                var reader = new MultipartReader(boundary, HttpContext.Request.Body);

                var section = await reader.ReadNextSectionAsync();

                while (section != null)
                {
                    var hasContentDispositionHeader =
                        ContentDispositionHeaderValue.TryParse(
                            section.ContentDisposition, out var contentDisposition);

                    if (hasContentDispositionHeader)
                    {
                        if (MultipartRequestHelper
                            .HasFileContentDisposition(contentDisposition))
                        {
                            untrustedFileNameForStorage = contentDisposition.FileName.Value;
                            // Don't trust the file name sent by the client. To display
                            // the file name, HTML-encode the value.
                            trustedFileNameForDisplay = WebUtility.HtmlEncode(
                                    contentDisposition.FileName.Value);

                            streamedFileContent =
                                await FileHelpers.ProcessStreamedFile(section, contentDisposition,
                                    ModelState, _permittedExtensions, _fileSizeLimit);

                            if (!ModelState.IsValid)
                            {
                                return BadRequest(ModelState);
                            }

                            //儲存檔案
                            using (var targetStream = System.IO.File.Create(Path.Combine(_targetFilePath, trustedFileNameForDisplay)))
                            {
                                await targetStream.WriteAsync(streamedFileContent);

                                _logger.LogInformation(
                                    "Uploaded file '{TrustedFileNameForDisplay}' saved to " +
                                    "'{TargetFilePath}' as {TrustedFileNameForFileStorage}",
                                    trustedFileNameForDisplay, _targetFilePath,
                                    trustedFileNameForDisplay);
                            }
                        }
                        else if (MultipartRequestHelper
                            .HasFormDataContentDisposition(contentDisposition))
                        {
                            // Don't limit the key name length because the 
                            // multipart headers length limit is already in effect.
                            var key = HeaderUtilities
                                .RemoveQuotes(contentDisposition.Name).Value;
                            var encoding = GetEncoding(section);

                            if (encoding == null)
                            {
                                ModelState.AddModelError("File",
                                    $"The request couldn't be processed (Error 2).");
                                // Log error

                                return BadRequest(ModelState);
                            }

                            using (var streamReader = new StreamReader(
                                section.Body,
                                encoding,
                                detectEncodingFromByteOrderMarks: true,
                                bufferSize: 1024,
                                leaveOpen: true))
                            {
                                // The value length limit is enforced by 
                                // MultipartBodyLengthLimit
                                var value = await streamReader.ReadToEndAsync();

                                if (string.Equals(value, "undefined",
                                    StringComparison.OrdinalIgnoreCase))
                                {
                                    value = string.Empty;
                                }

                                formAccumulator.Append(key, value);

                                if (formAccumulator.ValueCount >
                                    _defaultFormOptions.ValueCountLimit)
                                {
                                    // Form key count limit of 
                                    // _defaultFormOptions.ValueCountLimit 
                                    // is exceeded.
                                    ModelState.AddModelError("File",
                                        $"The request couldn't be processed (Error 3).");
                                    // Log error

                                    return BadRequest(ModelState);
                                }
                            }
                        }
                    }

                    // Drain any remaining section body that hasn't been consumed and
                    // read the headers for the next section.
                    section = await reader.ReadNextSectionAsync();
                }

                // Bind form data to the model
                var formData = new FormData();
                var formValueProvider = new FormValueProvider(
                    BindingSource.Form,
                    new FormCollection(formAccumulator.GetResults()),
                    CultureInfo.CurrentCulture);
                var bindingSuccessful = await TryUpdateModelAsync(formData, prefix: "",
                    valueProvider: formValueProvider);

                if (!bindingSuccessful)
                {
                    ModelState.AddModelError("File",
                        "The request couldn't be processed (Error 5).");
                    // Log error
                    return BadRequest(ModelState);
                }

                return Json(new { message = "success"});
            }
            catch(Exception e)
            {
                return BadRequest(e.Message);
            }
        }

        public class FormData
        {
            public string Message { get; set; }
        }

        public static Encoding GetEncoding(MultipartSection section)
        {
            MediaTypeHeaderValue mediaType;
            var hasMediaTypeHeader = MediaTypeHeaderValue.TryParse(section.ContentType, out mediaType);
            if (!hasMediaTypeHeader || Encoding.UTF7.Equals(mediaType.Encoding))
            {
                return Encoding.UTF8;
            }
            return mediaType.Encoding;
        }
    }
}
