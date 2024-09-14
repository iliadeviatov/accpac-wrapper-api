using AccpacCOMAPI;
using Microsoft.Extensions.Logging;
using Wrapper.Models.Accpac.APModels.ApInvoiceBatchModels;
using Wrapper.Models.Common;
using Wrapper.Models.Common.Exceptions;
using Wrapper.Services;
using Wrapper.Services.Accpac.APModule;
using Wrapper.Services.Accpac.APModule.APInvoiceBatchServices;
using Wrapper.Services.Common;
using Wrapper.Services.Utils;

namespace Wrapper.Accpac.APModule.APInvoiceBatchServices
{
    public class APInvoiceBatchEditor : IAPInvoiceBatchEditor
    {
        private readonly ILogger<APInvoiceBatchEditor> logger;
        private readonly IAPInvoiceBatchValidator aPInvoiceBatchValidator;
        private readonly IApInvoiceBatchViewComposer viewComposer;
        private readonly INotificationMessenger messenger;

        public APInvoiceBatchEditor(
                ILogger<APInvoiceBatchEditor> logger,
                IAPInvoiceBatchValidator aPInvoiceBatchValidator,
                IApInvoiceBatchViewComposer viewComposer,
                INotificationMessenger messenger
            )
        {
            this.logger = logger;
            this.aPInvoiceBatchValidator = aPInvoiceBatchValidator;
            this.viewComposer = viewComposer;
            this.messenger = messenger;
        }

        public async Task CreateBatchAsync(IOperationContext context, ApInvoiceBatchEntryModel model)
        {
            await aPInvoiceBatchValidator.ValidateCreateInvoiceBatchAsync(context, model);

            int currentEntry = 0;
            int totalEntries = model.Headers.Count;

            context.AccpacDBLink.TransactionBegin(out int p);

            try
            {
                ApInvoiceBatchView view = viewComposer.BuildBatchView(context);

                view.BatchView.RecordCreate(tagViewRecordCreateEnum.VIEW_RECORD_CREATE_INSERT);
                view.BatchView.Fields.FieldByName["BTCHDESC"].set_Value(model.BatchName);
                view.BatchView.Fields.FieldByName["DATEBTCH"].set_Value(model.BatchDate);
                view.BatchView.Update();

                foreach (ApInvoiceBatchHeaderEntryModel header in model.Headers)
                {
                    // notify progress
                    currentEntry++;
                    await messenger.NotifyProgressAsync(context, new NotificationModel
                    (
                        currentEntry, totalEntries, ProgressType.Import, LongRunningProcessType.APinvoiceAccpacPosting, context.UserId
                    ));

                    // create header
                    // process commands required to make sure, tax calculation error is not shown
                    view.HeaderView.RecordCreate(tagViewRecordCreateEnum.VIEW_RECORD_CREATE_NOINSERT);
                    view.HeaderView.Fields.FieldByName["DATEINVC"].set_Value(header.InvoiceDate);
                    view.HeaderView.Fields.FieldByName["TEXTTRX"].set_Value(header.TransactionType); // AP
                    view.HeaderView.Fields.FieldByName["PROCESSCMD"].set_Value("7");
                    view.HeaderView.Process();
                    view.HeaderView.Fields.FieldByName["PROCESSCMD"].set_Value("4");
                    view.HeaderView.Process();
                    view.HeaderView.Fields.FieldByName["IDVEND"].set_Value(header.VendorId);
                    view.HeaderView.Fields.FieldByName["PROCESSCMD"].set_Value("7");
                    view.HeaderView.Process();
                    view.HeaderView.Fields.FieldByName["IDINVC"].set_Value(header.InvoiceNo);
                    view.HeaderView.Fields.FieldByName["INVCDESC"].set_Value(header.Description);

                    // set gross total
                    view.HeaderView.Fields.FieldByName["AMTGROSTOT"].set_Value(header.TotalAmount);

                    view.HeaderView.Process();

                    // header optional fields

                    view.HeaderOptFieldView.RecordCreate(tagViewRecordCreateEnum.VIEW_RECORD_CREATE_NOINSERT);
                    view.HeaderOptFieldView.Fields.FieldByName["OPTFIELD"].set_Value("CREDITCODE");
                    view.HeaderOptFieldView.Fields.FieldByName["SWSET"].set_Value("1"); 
                    view.HeaderOptFieldView.Fields.FieldByName["VALIFTEXT"].set_Value(header.CreditCode);
                    view.HeaderOptFieldView.Insert();

                    foreach (ApInvoiceBatchDetailEntryModel detail in header.Details)
                    {
                        view.DetailView.RecordCreate(tagViewRecordCreateEnum.VIEW_RECORD_CREATE_NOINSERT);
                        view.DetailView.Fields.FieldByName["TEXTDESC"].set_Value(detail.Description);
                        view.DetailView.Fields.FieldByName["IDGLACCT"].set_Value(detail.AccountId.Trim());

                        view.DetailView.Fields.FieldByName["AMTDIST"].set_Value(detail.Amount);
                        view.DetailView.Fields.FieldByName["AMTGLDIST"].set_Value(detail.Amount);
                        view.DetailView.Fields.FieldByName["AMTDISTHC"].set_Value(detail.Amount);
                        view.DetailView.Fields.FieldByName["DISTNETHC"].set_Value(detail.Amount);

                        view.DetailView.Fields.FieldByName["RATETAX1"].set_Value(0);
                        view.DetailView.Fields.FieldByName["RATETAX2"].set_Value(0);
                        view.DetailView.Fields.FieldByName["AMTTOTTAX"].set_Value(0);

                        view.DetailView.Insert();

                        view.DetailView.RecordClear();
                    }

                    view.HeaderView.Fields.FieldByName["TAXCLASS1"].set_Value("3"); // no tax

                    view.HeaderView.Insert();
                    view.HeaderView.Update();
                    view.HeaderView.RecordClear();
                }

                view.BatchView.Update();

                context.AccpacDBLink.TransactionCommit(out p);
            }
            catch (Exception ex)
            {
                context.AccpacDBLink.TransactionRollback(out p);

                ErrorUtils.LogAndThrowAccpacException(context, logger, "Failed to create cashbook AP batch",
                    unhandledExceptionAction: () => throw new CreateEntityException(ex.Message, ex.InnerException));
            }
            finally
            {
                // make sure the progress is 100% even if an error occurs
                await messenger.NotifyProgressAsync(context, new NotificationModel
                (
                    totalEntries, totalEntries, ProgressType.Import, LongRunningProcessType.APinvoiceAccpacPosting, context.UserId
                ));
            }
        }
    }
}
