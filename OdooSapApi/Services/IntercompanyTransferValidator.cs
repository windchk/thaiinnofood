using OdooSapApi.Models;

namespace OdooSapApi.Services;

public static class IntercompanyTransferValidator
{
    public static void Validate(IntercompanyTransferRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.TransferId))
        {
            throw new ArgumentException("transferId is required.");
        }

        request.TransferId = request.TransferId.Trim();
        request.SiteId = request.SiteId?.Trim() ?? "";
        request.SourceCompanyName = request.SourceCompanyName?.Trim() ?? "";
        request.TargetCompanyName = request.TargetCompanyName?.Trim() ?? "";
        request.Remarks = string.IsNullOrWhiteSpace(request.Remarks)
            ? null
            : request.Remarks.Trim();

        if (request.Remarks?.Length > 150)
        {
            throw new ArgumentException("remarks must not exceed 150 characters.");
        }

        if (request.TransferId.Length > 80)
        {
            throw new ArgumentException("transferId must not exceed 80 characters.");
        }

        if (request.TransferId.Any(char.IsControl) || request.TransferId.Contains('|'))
        {
            throw new ArgumentException("transferId contains unsupported characters.");
        }

        if (string.IsNullOrWhiteSpace(request.SiteId))
        {
            throw new ArgumentException("siteId is required.");
        }

        if (request.SiteId.Length > 20)
        {
            throw new ArgumentException("siteId must not exceed 20 characters.");
        }

        if (string.IsNullOrWhiteSpace(request.SourceCompanyName))
        {
            throw new ArgumentException("sourceCompanyName is required.");
        }

        if (request.SourceCompanyName.Length > 128)
        {
            throw new ArgumentException("sourceCompanyName must not exceed 128 characters.");
        }

        if (string.IsNullOrWhiteSpace(request.TargetCompanyName))
        {
            throw new ArgumentException("targetCompanyName is required.");
        }

        if (request.TargetCompanyName.Length > 128)
        {
            throw new ArgumentException("targetCompanyName must not exceed 128 characters.");
        }

        if (string.Equals(
                request.SourceCompanyName,
                request.TargetCompanyName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Source and target company must be different.");
        }

        if (!request.PostingDate.HasValue)
        {
            throw new ArgumentException("postingDate is required.");
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            throw new ArgumentException("lines is required.");
        }

        if (request.Lines.Count > 100)
        {
            throw new ArgumentException("lines must not exceed 100 entries.");
        }

        var duplicateLineId = request.Lines
            .Where(x => !string.IsNullOrWhiteSpace(x.LineId))
            .GroupBy(x => x.LineId.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(x => x.Count() > 1);

        if (duplicateLineId is not null)
        {
            throw new ArgumentException(
                $"lines[].lineId must be unique. Duplicate lineId={duplicateLineId.Key}.");
        }

        foreach (var line in request.Lines)
        {
            ValidateLine(line);
        }
    }

    private static void ValidateLine(IntercompanyTransferLineRequest line)
    {
        line.LineId = line.LineId?.Trim() ?? "";
        line.ItemCode = line.ItemCode?.Trim() ?? "";
        line.SourceWarehouse = line.SourceWarehouse?.Trim() ?? "";
        line.TargetWarehouse = line.TargetWarehouse?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(line.LineId))
        {
            throw new ArgumentException("lines[].lineId is required.");
        }

        if (line.LineId.Length > 80)
        {
            throw new ArgumentException("lines[].lineId must not exceed 80 characters.");
        }

        if (string.IsNullOrWhiteSpace(line.ItemCode))
        {
            throw new ArgumentException("lines[].itemCode is required.");
        }

        if (line.ItemCode.Length > 50)
        {
            throw new ArgumentException("lines[].itemCode must not exceed 50 characters.");
        }

        if (line.Quantity <= 0)
        {
            throw new ArgumentException("lines[].quantity must be greater than 0.");
        }

        if (string.IsNullOrWhiteSpace(line.SourceWarehouse))
        {
            throw new ArgumentException("lines[].sourceWarehouse is required.");
        }

        if (line.SourceWarehouse.Length > 8)
        {
            throw new ArgumentException("lines[].sourceWarehouse must not exceed 8 characters.");
        }

        if (string.IsNullOrWhiteSpace(line.TargetWarehouse))
        {
            throw new ArgumentException("lines[].targetWarehouse is required.");
        }

        if (line.TargetWarehouse.Length > 8)
        {
            throw new ArgumentException("lines[].targetWarehouse must not exceed 8 characters.");
        }

        if (line.Batches is null || line.SourceBins is null || line.TargetBins is null)
        {
            throw new ArgumentException(
                "lines[].batches, lines[].sourceBins and lines[].targetBins cannot be null.");
        }

        if (line.Batches.Count > 0)
        {
            if (line.SourceBins.Count > 0 || line.TargetBins.Count > 0)
            {
                throw new ArgumentException(
                    "Use batches[].sourceBins/targetBins when batches is sent.");
            }

            if (line.Batches.Sum(x => x.Quantity) != line.Quantity)
            {
                throw new ArgumentException(
                    "Sum of lines[].batches[].quantity must equal line quantity.");
            }

            var duplicateBatch = line.Batches
                .Where(x => !string.IsNullOrWhiteSpace(x.BatchNumber))
                .GroupBy(x => x.BatchNumber.Trim(), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(x => x.Count() > 1);

            if (duplicateBatch is not null)
            {
                throw new ArgumentException(
                    $"Batch number must be unique within a line. Duplicate batchNumber={duplicateBatch.Key}.");
            }

            foreach (var batch in line.Batches)
            {
                ValidateBatch(batch);
            }

            return;
        }

        ValidateBins(line.SourceBins, line.Quantity, "lines[].sourceBins");
        ValidateBins(line.TargetBins, line.Quantity, "lines[].targetBins");
    }

    private static void ValidateBatch(IntercompanyTransferBatchRequest batch)
    {
        batch.BatchNumber = batch.BatchNumber?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(batch.BatchNumber))
        {
            throw new ArgumentException("lines[].batches[].batchNumber is required.");
        }

        if (batch.BatchNumber.Length > 32)
        {
            throw new ArgumentException(
                "lines[].batches[].batchNumber must not exceed 32 characters.");
        }

        if (batch.Quantity <= 0)
        {
            throw new ArgumentException("lines[].batches[].quantity must be greater than 0.");
        }

        if (batch.SourceBins is null || batch.TargetBins is null)
        {
            throw new ArgumentException(
                "lines[].batches[].sourceBins and targetBins cannot be null.");
        }

        if (batch.ExpiryDate.HasValue
            && batch.ManufacturingDate.HasValue
            && batch.ExpiryDate.Value.Date < batch.ManufacturingDate.Value.Date)
        {
            throw new ArgumentException(
                "Batch expiryDate cannot be earlier than manufacturingDate.");
        }

        ValidateBins(batch.SourceBins, batch.Quantity, "lines[].batches[].sourceBins");
        ValidateBins(batch.TargetBins, batch.Quantity, "lines[].batches[].targetBins");
    }

    private static void ValidateBins(
        List<ProductionBinAllocationRequest> bins,
        decimal expectedQuantity,
        string fieldName)
    {
        if (bins.Count == 0)
        {
            return;
        }

        if (bins.Sum(x => x.Quantity) != expectedQuantity)
        {
            throw new ArgumentException(
                $"Sum of {fieldName}[].quantity must equal related quantity.");
        }

        foreach (var bin in bins)
        {
            bin.BinCode = bin.BinCode?.Trim() ?? "";

            if (bin.Quantity <= 0)
            {
                throw new ArgumentException(
                    $"{fieldName}[].quantity must be greater than 0.");
            }

            if (!bin.BinAbsEntry.HasValue && string.IsNullOrWhiteSpace(bin.BinCode))
            {
                throw new ArgumentException(
                    $"{fieldName}[].binAbsEntry or binCode is required.");
            }

            if (bin.BinAbsEntry.HasValue && bin.BinAbsEntry.Value <= 0)
            {
                throw new ArgumentException(
                    $"{fieldName}[].binAbsEntry must be greater than 0.");
            }

            if (bin.BinCode.Length > 228)
            {
                throw new ArgumentException(
                    $"{fieldName}[].binCode must not exceed 228 characters.");
            }
        }
    }
}
