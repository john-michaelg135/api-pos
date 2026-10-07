$servicePath = "Applications\Services\OrderManagementService.cs"
$serviceContent = Get-Content $servicePath -Raw
$voidLogic = @"
    public async Task<OrderManagementResponseDto?> RequestVoidAsync(int orderId, string reason)
    {
        var order = await _db.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Include(o => o.Payments)
            .Include(o => o.Location)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.OrderId == orderId);
        if (order == null) return null;

        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (currentUser?.SubRole == `"Cashier`" && order.LocationId != currentUser.LocationId && order.OrderSource != `"Ecommerce`")
            throw new InvalidOperationException(`"You are not authorized to perform this action for other locations or ecommerce orders.`");

        if (order.OrderStatus == `"Void Requested`" || order.OrderStatus == `"Voided`" || order.OrderStatus == `"Cancelled`" || order.OrderStatus == `"Refund Requested`" || order.OrderStatus == `"Refunded`")
            throw new InvalidOperationException(`$"Order {order.OrderNumber} is {order.OrderStatus}. Cannot request void.`");

        var currentStatus = order.OrderStatus;
        order.OrderStatus = `"Void Requested`";
        order.RejectionRemarks = reason;
        order.UpdatedAt = DateTime.UtcNow;

        await RecordStatusHistoryAsync(order.OrderId, currentStatus, `"Void Requested`", 1, reason);
        _db.Orders.Update(order);
        await _db.SaveChangesAsync();

        return MapToResponse(order);
    }

    public async Task<OrderManagementResponseDto?> ApproveVoidAsync(int orderId, int approvedBy)
    {
        var order = await _db.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Include(o => o.Payments)
            .Include(o => o.Location)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.OrderId == orderId);
            
        if (order == null) return null;

        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (currentUser?.SubRole == `"Cashier`" && order.LocationId != currentUser.LocationId && order.OrderSource != `"Ecommerce`")
            throw new InvalidOperationException(`"You are not authorized to perform this action for other locations or ecommerce orders.`");

        if (order.OrderStatus != `"Void Requested`")
            throw new InvalidOperationException(`$"Order {order.OrderNumber} is {order.OrderStatus}. Only Void Requested orders can be approved.`");

        var currentStatus = order.OrderStatus;
        order.OrderStatus = `"Voided`";
        order.ApprovedBy = approvedBy;
        order.ApprovedAt = DateTime.UtcNow;
        order.UpdatedAt = DateTime.UtcNow;

        await RecordStatusHistoryAsync(order.OrderId, currentStatus, `"Voided`", approvedBy, `"Void approved`");
        _db.Orders.Update(order);
        await _db.SaveChangesAsync();

        if (order.LocationId.HasValue)
        {
            foreach (var item in order.OrderItems)
            {
                try
                {
                    await _inventoryService.RestoreStockAsync(item.VariationId, order.LocationId.Value, item.Quantity);
                }
                catch (InvalidOperationException) {}
            }
        }

        return MapToResponse(order);
    }

    public async Task<OrderManagementResponseDto?> RejectVoidAsync(int orderId, int rejectedBy, string reason)
    {
        var order = await _db.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Include(o => o.Payments)
            .Include(o => o.Location)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.OrderId == orderId);
        if (order == null) return null;

        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (currentUser?.SubRole == `"Cashier`" && order.LocationId != currentUser.LocationId && order.OrderSource != `"Ecommerce`")
            throw new InvalidOperationException(`"You are not authorized to perform this action for other locations or ecommerce orders.`");

        if (order.OrderStatus != `"Void Requested`")
            throw new InvalidOperationException(`$"Order {order.OrderNumber} is {order.OrderStatus}. Only Void Requested orders can be rejected.`");

        var currentStatus = order.OrderStatus;
        
        var previousStatusRecord = await _db.OrderStatusHistories
            .Where(h => h.OrderId == orderId && h.NewStatus == `"Void Requested`")
            .OrderByDescending(h => h.CreatedAt)
            .FirstOrDefaultAsync();
            
        string revertStatus = previousStatusRecord?.OldStatus ?? `"Completed`";
        
        order.OrderStatus = revertStatus;
        order.ApprovedBy = rejectedBy; 
        order.RejectionRemarks = `$"Void Rejected: {reason}`";
        order.UpdatedAt = DateTime.UtcNow;

        await RecordStatusHistoryAsync(order.OrderId, currentStatus, revertStatus, rejectedBy, `$"Void rejected: {reason}`");
        _db.Orders.Update(order);
        await _db.SaveChangesAsync();

        return MapToResponse(order);
    }
"@
$serviceContent = $serviceContent.Replace("private async Task RecordStatusHistoryAsync(", "$voidLogic`r`n`r`n    private async Task RecordStatusHistoryAsync(")
Set-Content -Path $servicePath -Value $serviceContent -NoNewline

$controllerPath = "Api\Controllers\OrderManagementController.cs"
$controllerContent = Get-Content $controllerPath -Raw
$controllerLogic = @"
    // PUT api-pos/order-management/orders/{orderId}/request-void
    [HttpPut(`"orders/{orderId:int}/request-void`")]
    [ProducesResponseType(typeof(OrderManagementResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RequestVoid(int orderId, [FromBody] RequestRefundDto dto)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.Reason)) return BadRequest(`"Void reason is required.`");
        try
        {
            var order = await _orderManagementService.RequestVoidAsync(orderId, dto.Reason);
            if (order == null) return NotFound(`$"Order with ID {orderId} not found.`");
            return Ok(order);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    // PUT api-pos/order-management/orders/{orderId}/approve-void
    [HttpPut(`"orders/{orderId:int}/approve-void`")]
    [ProducesResponseType(typeof(OrderManagementResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ApproveVoid(int orderId, [FromBody] ApproveOrderDto dto)
    {
        if (dto == null) return BadRequest(`"Approval data is required.`");
        try
        {
            var order = await _orderManagementService.ApproveVoidAsync(orderId, dto.ApprovedBy);
            if (order == null) return NotFound(`$"Order with ID {orderId} not found.`");
            return Ok(order);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    // PUT api-pos/order-management/orders/{orderId}/reject-void
    [HttpPut(`"orders/{orderId:int}/reject-void`")]
    [ProducesResponseType(typeof(OrderManagementResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RejectVoid(int orderId, [FromBody] RejectOrderDto dto)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.Reason)) return BadRequest(`"Rejection reason is required.`");
        try
        {
            var order = await _orderManagementService.RejectVoidAsync(orderId, dto.RejectedBy, dto.Reason);
            if (order == null) return NotFound(`$"Order with ID {orderId} not found.`");
            return Ok(order);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }
"@
$controllerContent = $controllerContent.Replace("    // ── EC-013: Dashboard metrics ──", "$controllerLogic`r`n`r`n    // ── EC-013: Dashboard metrics ──")
Set-Content -Path $controllerPath -Value $controllerContent -NoNewline
