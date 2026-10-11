using System;
using System.Threading;
using System.Threading.Tasks;

namespace DocConverter.Helpers
{
    /// <summary>
    /// İşi kendi STA iş parçacığında yürütür. Office otomasyon nesneleri apartment-threaded'dir; iş parçacığı
    /// havuzunda (MTA) kullanıldıklarında vekil üzerinden çalışır ve serbest bırakılmaları gecikir. PowerPoint bu
    /// yüzden her dönüştürmeden sonra gizli bir işlem olarak açık kalıyordu.
    /// </summary>
    public static class StaThread
    {
        public static Task RunAsync(Action work, string name, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(work);

            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var thread = new Thread(() =>
            {
                try
                {
                    work();
                    completion.SetResult();
                }
                catch (OperationCanceledException ex)
                {
                    completion.SetCanceled(ex.CancellationToken);
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            })
            {
                IsBackground = true,
                Name = name
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return completion.Task;
        }
    }
}
