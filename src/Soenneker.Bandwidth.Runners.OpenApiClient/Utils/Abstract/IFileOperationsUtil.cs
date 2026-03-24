using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Bandwidth.Runners.OpenApiClient.Utils.Abstract;

public interface IFileOperationsUtil
{
    ValueTask Process(CancellationToken cancellationToken = default);
}
